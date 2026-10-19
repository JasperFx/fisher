using System.Data.Common;
using Weasel.Core.Sequences;
using Weasel.Storage;

namespace Fisher.Storage.Sequences;

/// <summary>
///     fisher#454. What a session enlisted in a caller's transaction hands its storages as
///     <see cref="IStorageSession.Database" />: the session's own <see cref="FisherDatabase" /> in
///     every respect but one — a Hi-Lo sequence reached through it refuses to reserve a new block, which
///     it would otherwise do on a connection of its own, waiting on the write lock the caller's
///     transaction holds. See <see cref="HiloSequence.RefuseAdvances" />.
/// </summary>
internal sealed class EnlistedStorageDatabase(FisherDatabase inner) : IStorageDatabase
{
    private readonly IStorageDatabase _inner = inner;

    public IProviderGraph Providers => _inner.Providers;

    public DbConnection CreateStorageConnection() => _inner.CreateStorageConnection();

    public Task RunSqlAsync(string sql, CancellationToken ct = default) => _inner.RunSqlAsync(sql, ct);

    public ISequence SequenceFor(Type documentType)
        => new EnlistedSequence(_inner.SequenceFor(documentType), documentType);

    private sealed class EnlistedSequence(ISequence inner, Type documentType) : ISequence
    {
        public int MaxLo => inner.MaxLo;

        public int NextInt()
        {
            using var _ = HiloSequence.RefuseAdvances(documentType);
            return inner.NextInt();
        }

        public long NextLong()
        {
            using var _ = HiloSequence.RefuseAdvances(documentType);
            return inner.NextLong();
        }

        public Task SetFloor(long floor) => inner.SetFloor(floor);
    }
}
