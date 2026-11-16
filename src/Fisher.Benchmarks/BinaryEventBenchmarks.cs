using System.Buffers;
using BenchmarkDotNet.Attributes;
using Fisher.TestUtils;
using JasperFx.Events;
using MemoryPack;
using Microsoft.Data.Sqlite;

namespace Fisher.Benchmarks;

// fisher#570 — does adopting JasperFx 3.3.0's IBufferWriter / span members on IEventBinarySerializer
// (jasperfx#1187/#1190) save anything on Fisher's binary event path? Four measurements, each isolating one
// share of the cost:
//
//   BinaryEncodeBenchmarks       the serializer step alone, write and read, today's member vs the new one
//   BinaryColumnReadBenchmarks   reading the BLOB column out of Microsoft.Data.Sqlite, today's GetValue vs
//                                the span-shaped alternatives the provider offers
//   BinaryEventStoreBenchmarks   end to end through a real Fisher store: append + commit, FetchStreamAsync
//   BinaryParameterProbe         (not BDN) which BLOB parameter shapes the provider will bind at all
//
// Every measurement runs two serializers over the same library (MemoryPack) and the same bytes, so the only
// difference between them is which IEventBinarySerializer members they implement: one implements only the
// byte[] pair and lets the interface defaults serve the new members, the other overrides both new members
// and writes straight into the IBufferWriter / reads straight off the span.

/// <summary>The binary event every fisher#570 measurement appends and reads.</summary>
[MemoryPackable]
public partial class BinaryBenchEvent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Sequence { get; set; }
    public byte[] Payload { get; set; } = [];

    /// <summary>A body whose serialized size is roughly <paramref name="payloadBytes" /> plus ~60 bytes.</summary>
    public static BinaryBenchEvent Create(int sequence, int payloadBytes)
    {
        var payload = new byte[payloadBytes];
        new Random(sequence).NextBytes(payload);
        return new BinaryBenchEvent
        {
            Id = Guid.NewGuid(),
            Name = $"binary-event-{sequence}",
            Sequence = sequence,
            Payload = payload
        };
    }
}

public enum BinarySerializerShape
{
    /// <summary>Implements only <c>Serialize(Type, object)</c> / <c>Deserialize(Type, byte[])</c>.</summary>
    ByteArrayOnly,

    /// <summary>Also overrides the <c>IBufferWriter</c> and <c>ReadOnlySpan</c> members (the MemoryPack case).</summary>
    BufferWriterAndSpan
}

/// <summary>A serializer that implements only the byte[] pair; the interface defaults serve the new members.</summary>
public sealed class ByteArrayOnlyMemoryPackSerializer : IEventBinarySerializer
{
    public byte[] Serialize(Type type, object data) => MemoryPackSerializer.Serialize(type, data);

    public object Deserialize(Type type, byte[] data) => MemoryPackSerializer.Deserialize(type, data)!;
}

/// <summary>A serializer that overrides both jasperfx#1187 members and writes into / reads from the caller's memory.</summary>
public sealed class BufferWriterMemoryPackSerializer : IEventBinarySerializer
{
    public byte[] Serialize(Type type, object data) => MemoryPackSerializer.Serialize(type, data);

    public object Deserialize(Type type, byte[] data) => MemoryPackSerializer.Deserialize(type, data)!;

    public void Serialize(Type type, object data, IBufferWriter<byte> destination)
        => MemoryPackSerializer.Serialize(type, destination, data);

    public object Deserialize(Type type, ReadOnlySpan<byte> data) => MemoryPackSerializer.Deserialize(type, data)!;
}

internal static class BinarySerializers
{
    public static IEventBinarySerializer For(BinarySerializerShape shape) => shape switch
    {
        BinarySerializerShape.ByteArrayOnly => new ByteArrayOnlyMemoryPackSerializer(),
        _ => new BufferWriterMemoryPackSerializer()
    };
}

/// <summary>
///     The serializer step alone, per event: what Fisher calls today against what an adoption would call.
/// </summary>
/// <remarks>
///     <para>
///         <c>BufferWriterThenToArray</c> is the realistic adoption on the write side: Microsoft.Data.Sqlite
///         binds a BLOB only from an exact-length <c>byte[]</c> (see <see cref="BinaryParameterProbe" />), so
///         bytes written into a pooled buffer still have to be copied out to a new array for the parameter.
///         <c>BufferWriterNoCopy</c> stops before that copy: it is the floor an adoption could reach only if
///         the provider bound a span, which it does not.
///     </para>
///     <para>
///         The writer is one reused <see cref="ArrayBufferWriter{T}" />, which is the best case for pooling
///         (no rent, no return, already grown to size). A real adoption would pay a little more.
///     </para>
/// </remarks>
[Config(typeof(MicroBenchmarkConfig))]
public class BinaryEncodeBenchmarks
{
    private readonly ArrayBufferWriter<byte> _writer = new(256);
    private IEventBinarySerializer _serializer = null!;
    private BinaryBenchEvent _event = null!;
    private byte[] _encoded = null!;

    [Params(BinarySerializerShape.ByteArrayOnly, BinarySerializerShape.BufferWriterAndSpan)]
    public BinarySerializerShape Serializer { get; set; }

    [Params(200, 100_000)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _serializer = BinarySerializers.For(Serializer);
        _event = BinaryBenchEvent.Create(1, PayloadBytes);
        _encoded = _serializer.Serialize(typeof(BinaryBenchEvent), _event);

        // Grow the reused writer to size so the measured calls never pay its growth.
        _serializer.Serialize(typeof(BinaryBenchEvent), _event, _writer);
        _writer.ResetWrittenCount();
    }

    /// <summary>Fisher today (<c>EventGraph.BinaryEncoderFor</c>): one exact-length array per event.</summary>
    [Benchmark(Baseline = true)]
    public byte[] Serialize_ByteArray() => _serializer.Serialize(typeof(BinaryBenchEvent), _event);

    /// <summary>The adoption: serialize into a pooled writer, then copy out the byte[] the BLOB bind needs.</summary>
    [Benchmark]
    public byte[] Serialize_BufferWriterThenToArray()
    {
        _writer.ResetWrittenCount();
        _serializer.Serialize(typeof(BinaryBenchEvent), _event, _writer);
        return _writer.WrittenSpan.ToArray();
    }

    /// <summary>The unreachable floor: serialize into a pooled writer and stop. The provider cannot bind this.</summary>
    [Benchmark]
    public int Serialize_BufferWriterNoCopy()
    {
        _writer.ResetWrittenCount();
        _serializer.Serialize(typeof(BinaryBenchEvent), _event, _writer);
        return _writer.WrittenCount;
    }

    /// <summary>Fisher today (<c>FisherEventsRowReader.DeserializeBinary</c>).</summary>
    [Benchmark]
    public object Deserialize_ByteArray() => _serializer.Deserialize(typeof(BinaryBenchEvent), _encoded);

    /// <summary>The span member over the same array — what an adoption reading via <c>GetValue</c> would call.</summary>
    [Benchmark]
    public object Deserialize_Span()
        => _serializer.Deserialize(typeof(BinaryBenchEvent), new ReadOnlySpan<byte>(_encoded));
}

/// <summary>
///     The read side's share: getting a BLOB column's bytes out of Microsoft.Data.Sqlite and decoding them,
///     per row, in the three ways the provider offers.
/// </summary>
/// <remarks>
///     <para>
///         <c>GetValue</c> is what Fisher does. The two <c>GetBytes</c> variants read into a rented buffer and
///         decode the span, which is the only way to hand the span member memory the provider did not allocate.
///         They differ in whether the SELECT carries the row's rowid: Fisher's always does, because
///         <c>seq_id</c> is an <c>INTEGER PRIMARY KEY</c> and therefore the rowid's alias, and with it the
///         provider serves <c>GetBytes</c> through incremental blob I/O (a <c>sqlite3_blob_open</c> per row)
///         rather than from the column it already read.
///     </para>
///     <para>
///         Deserialization uses the overriding serializer throughout, so the variants differ only in how the
///         bytes are reached.
///     </para>
/// </remarks>
[Config(typeof(MicroBenchmarkConfig))]
public class BinaryColumnReadBenchmarks
{
    private const int Rows = 500;

    private TemporaryDatabase _database = null!;
    private SqliteConnection _connection = null!;
    private readonly IEventBinarySerializer _serializer = new BufferWriterMemoryPackSerializer();

    [Params(200, 100_000)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _database = TemporaryDatabase.Create("bdn-binary-read");
        _connection = new SqliteConnection(_database.ConnectionString);
        _connection.Open();

        using (var create = _connection.CreateCommand())
        {
            create.CommandText =
                "create table bench_events (seq_id integer primary key autoincrement, data_binary blob)";
            create.ExecuteNonQuery();
        }

        using var transaction = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "insert into bench_events (data_binary) values ($body)";
        var body = insert.Parameters.Add("$body", SqliteType.Blob);
        for (var i = 0; i < Rows; i++)
        {
            body.Value = _serializer.Serialize(typeof(BinaryBenchEvent), BinaryBenchEvent.Create(i, PayloadBytes));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
        _database.Dispose();
    }

    /// <summary>Fisher today: <c>(byte[])GetValue</c>, then <c>Deserialize(Type, byte[])</c>. Rowid selected.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Rows)]
    public int GetValue_ByteArray()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "select seq_id, data_binary from bench_events";
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            var bytes = (byte[])reader.GetValue(1);
            _serializer.Deserialize(typeof(BinaryBenchEvent), bytes);
            count++;
        }

        return count;
    }

    /// <summary>GetBytes into a rented buffer, then the span member. Rowid selected, as Fisher's SELECT always has.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int GetBytes_RentedSpan_WithRowid() => ReadIntoRentedBuffer("select seq_id, data_binary from bench_events", 1);

    /// <summary>The same with no rowid in the SELECT — a shape Fisher's reads cannot take.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int GetBytes_RentedSpan_NoRowid() => ReadIntoRentedBuffer("select data_binary from bench_events", 0);

    /// <summary>
    ///     The only shape a read-side adoption could take: probe the length, and read into a rented buffer only
    ///     above the large-object-heap threshold, where an exact-length array is a Gen2 allocation. Below it,
    ///     <c>GetValue</c> as today. Rowid selected. What this costs a small body is the length probe.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Hybrid_RentedAboveLohThreshold()
    {
        const int threshold = 85_000;

        using var command = _connection.CreateCommand();
        command.CommandText = "select seq_id, data_binary from bench_events";
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            var length = (int)reader.GetBytes(1, 0, null, 0, 0);
            if (length < threshold)
            {
                _serializer.Deserialize(typeof(BinaryBenchEvent), (byte[])reader.GetValue(1));
            }
            else
            {
                var rented = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    reader.GetBytes(1, 0, rented, 0, length);
                    _serializer.Deserialize(typeof(BinaryBenchEvent), new ReadOnlySpan<byte>(rented, 0, length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            count++;
        }

        return count;
    }

    private int ReadIntoRentedBuffer(string sql, int ordinal)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            var length = (int)reader.GetBytes(ordinal, 0, null, 0, 0);
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                reader.GetBytes(ordinal, 0, rented, 0, length);
                _serializer.Deserialize(typeof(BinaryBenchEvent), new ReadOnlySpan<byte>(rented, 0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            count++;
        }

        return count;
    }
}

/// <summary>
///     End to end through a real Fisher store on a throwaway file: one commit of ten binary events onto a new
///     stream, and one <c>FetchStreamAsync</c> of a ten-event binary stream. This is the number a user sees,
///     and it is what the encode/decode step's share has to be judged against.
/// </summary>
[Config(typeof(MicroBenchmarkConfig))]
public class BinaryEventStoreBenchmarks
{
    private const int EventsPerCommit = 10;

    private TemporaryDatabase _database = null!;
    private DocumentStore _store = null!;
    private object[] _events = null!;
    private Guid _seededStream;

    [Params(BinarySerializerShape.ByteArrayOnly, BinarySerializerShape.BufferWriterAndSpan)]
    public BinarySerializerShape Serializer { get; set; }

    [Params(200, 100_000)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _database = TemporaryDatabase.Create("bdn-binary-store");
        var serializer = BinarySerializers.For(Serializer);
        _store = Scenarios.Harness.BuildStore(_database,
            options => options.Events.UseBinarySerializer<BinaryBenchEvent>(serializer));
        await _store.ApplyAllConfiguredChangesToDatabaseAsync();

        _events = new object[EventsPerCommit];
        for (var i = 0; i < EventsPerCommit; i++)
        {
            _events[i] = BinaryBenchEvent.Create(i, PayloadBytes);
        }

        _seededStream = Guid.NewGuid();
        await using var seed = _store.LightweightSession();
        seed.Events.StartStream(_seededStream, _events);
        await seed.SaveChangesAsync();

        // Pay the first read's lazily-built pieces here rather than in the first measured invocation.
        await using var warm = _store.QuerySession();
        await warm.Events.FetchStreamAsync(_seededStream);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>Start a new stream with ten binary events and commit it.</summary>
    [Benchmark]
    public async Task AppendTenAndCommit()
    {
        await using var session = _store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), _events);
        await session.SaveChangesAsync();
    }

    /// <summary>Read a ten-event binary stream back.</summary>
    [Benchmark]
    public async Task<int> FetchTen()
    {
        await using var session = _store.QuerySession();
        var events = await session.Events.FetchStreamAsync(_seededStream);
        return events.Count;
    }
}

/// <summary>
///     Which BLOB parameter shapes Microsoft.Data.Sqlite will bind. Not a timing: a write-side adoption that
///     avoided the copy out of a pooled buffer would need the provider to bind something other than an
///     exact-length <c>byte[]</c>, so this asks it.
/// </summary>
public static class BinaryParameterProbe
{
    public static void Run()
    {
        using var database = TemporaryDatabase.Create("binary-bind-probe");
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "create table probe (body blob)";
            create.ExecuteNonQuery();
        }

        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        Console.WriteLine($"Microsoft.Data.Sqlite {typeof(SqliteConnection).Assembly.GetName().Version}");
        Probe(connection, "byte[] (exact length)", bytes);
        Probe(connection, "ArraySegment<byte>", new ArraySegment<byte>(bytes, 0, 4));
        Probe(connection, "ReadOnlyMemory<byte>", new ReadOnlyMemory<byte>(bytes, 0, 4));
        Probe(connection, "Memory<byte>", new Memory<byte>(bytes, 0, 4));
        Probe(connection, "MemoryStream", new MemoryStream(bytes, 0, 4));
        Probe(connection, "byte[] with SqliteParameter.Size = 4", bytes, size: 4);

        // SqliteParameter.Size truncates a byte[], so an oversized rented array could be bound as-is. Whether
        // that saves the copy depends on how the provider truncates, so measure the bind's own allocation.
        Console.WriteLine();
        Console.WriteLine("Allocation per INSERT of a 100,000-byte BLOB (the array itself is pre-allocated):");
        MeasureBind(connection, "exact-length byte[]", new byte[100_000], size: null);
        MeasureBind(connection, "128 KiB byte[] with Size = 100,000", new byte[131_072], size: 100_000);
    }

    private static void MeasureBind(SqliteConnection connection, string shape, byte[] value, int? size)
    {
        const int iterations = 200;

        using var insert = connection.CreateCommand();
        insert.CommandText = "insert into probe (body) values ($body)";
        var parameter = insert.Parameters.Add("$body", SqliteType.Blob);
        parameter.Value = value;
        if (size is { } s)
        {
            parameter.Size = s;
        }

        insert.ExecuteNonQuery(); // warm

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            insert.ExecuteNonQuery();
        }

        var perInsert = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        Console.WriteLine($"  {shape,-38} {perInsert,10:n0} bytes");
    }

    private static void Probe(SqliteConnection connection, string shape, object value, int? size = null)
    {
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "delete from probe";
            clear.ExecuteNonQuery();
        }

        try
        {
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "insert into probe (body) values ($body)";
                var parameter = insert.Parameters.AddWithValue("$body", value);
                if (size is { } s)
                {
                    parameter.Size = s;
                }

                insert.ExecuteNonQuery();
            }

            using var read = connection.CreateCommand();
            read.CommandText = "select typeof(body), length(body), hex(body) from probe";
            using var reader = read.ExecuteReader();
            reader.Read();
            Console.WriteLine(
                $"  {shape,-38} bound as {reader.GetString(0)}, length {reader.GetInt64(1)}, hex {reader.GetString(2)}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"  {shape,-38} REFUSED: {e.GetType().Name}: {e.Message}");
        }
    }
}
