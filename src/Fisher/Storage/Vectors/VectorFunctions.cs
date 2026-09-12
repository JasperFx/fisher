using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using JasperFx.Events.Vectors;
using Weasel.Sqlite.Functions;

namespace Fisher.Storage.Vectors;

/// <summary>
///     The application-defined SQLite functions vector search runs on, registered on every
///     connection the store opens through <see cref="StoreOptions.Functions" /> (fisher#241).
/// </summary>
/// <remarks>
///     <para>
///         <c>fi_vector_distance(metric, json, query)</c>: <c>metric</c> is <c>'cosine'</c>,
///         <c>'l2'</c> or <c>'inner'</c>; <c>json</c> is the stored embedding as the JSON array
///         <c>json_extract</c> hands back; <c>query</c> is the caller's vector as a little-endian
///         float32 BLOB. Every metric is a DISTANCE — smaller is closer — including inner product,
///         which is negated, so one <c>ORDER BY</c> serves all three; that is the promise
///         <see cref="DistanceFunction" /> makes on every store.
///     </para>
///     <para>
///         The JSON is parsed on every row, and that is the cost of reading from <c>data</c> rather
///         than a BLOB column that could drift (see <see cref="VectorIndex" />). A 768-float array
///         parses in tens of microseconds, so the scan is milliseconds at thousands of rows and well
///         under a second at tens of thousands — Fisher's scale. A stored vector whose length differs
///         from the query's fails the row loudly rather than scoring it wrong.
///     </para>
/// </remarks>
internal static class VectorFunctions
{
    internal const string DistanceFunctionName = "fi_vector_distance";

    internal static void Register(SqliteFunctionRegistry functions)
    {
        functions.AddScalar<double?>(DistanceFunctionName,
            (object? metric, object? json, object? query) => Distance(metric as string, json as string, query as byte[]),
            isDeterministic: true);
    }

    internal static string MetricName(DistanceFunction distance) => distance switch
    {
        DistanceFunction.Cosine => "cosine",
        DistanceFunction.L2 => "l2",
        DistanceFunction.InnerProduct => "inner",
        _ => throw new ArgumentOutOfRangeException(nameof(distance), distance, null)
    };

    /// <summary>The query vector in the shape the function reads: little-endian float32.</summary>
    internal static byte[] ToBlob(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        MemoryMarshal.AsBytes(vector).CopyTo(bytes);
        if (!BitConverter.IsLittleEndian)
        {
            for (var i = 0; i < bytes.Length; i += sizeof(float)) Array.Reverse(bytes, i, sizeof(float));
        }

        return bytes;
    }

    internal static double? Distance(string? metric, string? json, byte[]? query)
    {
        if (json is null || query is null) return null;
        if (query.Length % sizeof(float) != 0)
        {
            throw new ArgumentException($"{DistanceFunctionName}: the query BLOB is not a whole number of float32 values");
        }

        var dimensions = query.Length / sizeof(float);
        var stored = ArrayPool<float>.Shared.Rent(dimensions);
        try
        {
            var count = ParseJsonArray(json, stored, dimensions);
            if (count != dimensions)
            {
                throw new ArgumentException(
                    $"{DistanceFunctionName}: the stored vector has {count} dimensions but the query has {dimensions}");
            }

            var q = MemoryMarshal.Cast<byte, float>(query);
            var s = stored.AsSpan(0, dimensions);

            return metric switch
            {
                "cosine" => Cosine(s, q),
                "l2" => L2(s, q),
                "inner" => NegativeInner(s, q),
                _ => throw new ArgumentException($"{DistanceFunctionName}: metric '{metric}' must be cosine, l2 or inner")
            };
        }
        finally
        {
            ArrayPool<float>.Shared.Return(stored);
        }
    }

    /// <summary>
    ///     Reads a JSON array of numbers into <paramref name="into" />, returning how many there were.
    ///     Stops counting past <paramref name="capacity" /> but keeps counting, so a length mismatch is
    ///     reported as the real length rather than as the capacity.
    /// </summary>
    private static int ParseJsonArray(string json, float[] into, int capacity)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            throw new ArgumentException($"{DistanceFunctionName}: the stored value is not a JSON array");
        }

        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number)
            {
                throw new ArgumentException($"{DistanceFunctionName}: the stored array holds a non-numeric element");
            }

            if (count < capacity) into[count] = reader.GetSingle();
            count++;
        }

        return count;
    }

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        if (na == 0 || nb == 0) return 1.0;
        return 1.0 - dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static double L2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = (double)a[i] - b[i];
            sum += d * d;
        }

        return Math.Sqrt(sum);
    }

    private static double NegativeInner(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += (double)a[i] * b[i];
        return -dot;
    }
}
