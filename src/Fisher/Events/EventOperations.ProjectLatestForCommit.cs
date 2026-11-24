using Fisher.Internal;
using JasperFx.Events;

namespace Fisher.Events;

public partial class EventOperations
{
    /// <summary>
    ///     The version each stream's pending events were stamped from by <c>ProjectLatestForCommit</c>, which the
    ///     commit must still start them from (fisher#685).
    /// </summary>
    private readonly Dictionary<StreamAction, long> _versionsPinnedForCommit = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     Project the aggregate <typeparamref name="T" /> for the stream, including the events pending in this
    ///     session, exactly as <c>SaveChangesAsync()</c> will write it: the pending events are given now the
    ///     versions, timestamps, user name, correlation id, causation id and headers that the commit then keeps.
    ///     Nothing is stored. Behaves as <see cref="ProjectLatest{T}(Guid,CancellationToken)" /> when the stream
    ///     has no pending events.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Marten's <c>ProjectLatestForCommit</c> (marten#6149), for an endpoint that stores its response in the
    ///         same transaction as its work (wolverine#5721). It rests on the commit keeping what is already set:
    ///         <see cref="JasperFx.Events.Appending.ClientSideAppendPlanner.Stamp" /> leaves an event's timestamp and
    ///         metadata alone, and re-derives the same versions under the write lock or refuses the commit on the
    ///         expected version.
    ///     </para>
    ///     <para>
    ///         Throws <see cref="InvalidOperationException" /> when the stream's version is not known before commit:
    ///         start it with <c>StartStream</c>, fetch it with <c>FetchForWriting</c>, or append with an expected
    ///         version. It takes the stream's id; for a natural key, fetch the stream with
    ///         <c>FetchForWritingByNaturalKey</c> first and pass its <c>Id</c>.
    ///     </para>
    ///     <para>
    ///         The commit refuses a stream whose expected version changed after the call, by fetching it for writing
    ///         again or appending with a new expected version; call this again after the last change. The event
    ///         sequence is still assigned at commit, events appended after the call are not covered by it, and
    ///         session metadata not set at the call is filled in at commit.
    ///     </para>
    /// </remarks>
    public ValueTask<T?> ProjectLatestForCommit<T>(Guid id, CancellationToken cancellation = default) where T : class
    {
        AssertGuidIdentity();
        PrepareEventsAsTheCommitWill(id);
        return ProjectLatestAsync<T>(id, cancellation);
    }

    /// <inheritdoc cref="ProjectLatestForCommit{T}(Guid,CancellationToken)" />
    public ValueTask<T?> ProjectLatestForCommit<T>(string id, CancellationToken cancellation = default) where T : class
    {
        AssertStringIdentity();
        PrepareEventsAsTheCommitWill(id);
        return ProjectLatestAsync<T>(id, cancellation);
    }

    private void PrepareEventsAsTheCommitWill(object streamId)
    {
        if (!_streams.TryGetValue(streamId, out var stream) || stream.Events.Count == 0)
        {
            return;
        }

        long startingVersion;
        if (stream.ActionType == StreamActionType.Start)
        {
            startingVersion = 0;
        }
        else if (stream.ExpectedVersionOnServer is { } expected)
        {
            startingVersion = expected;
        }
        else
        {
            throw new InvalidOperationException(
                $"ProjectLatestForCommit() cannot know the versions of the events pending for stream "
                + $"'{(object?)stream.Key ?? stream.Id}' until the session commits. Use FetchForWriting(), "
                + "StartStream(), or Append() with an expected version, or ProjectLatest() if the event metadata "
                + "is not needed.");
        }

        new AppendPlanner(_session).StampForCommit(stream, startingVersion);
        _versionsPinnedForCommit[stream] = startingVersion;
    }

    /// <summary>
    ///     Refuse a commit whose stream changed its expected version after <c>ProjectLatestForCommit</c>, before
    ///     anything is written (fisher#685, as marten#6149). Without it the commit would succeed with versions the
    ///     returned aggregate does not have.
    /// </summary>
    internal void AssertVersionsPinnedForCommitAreUnchanged()
    {
        if (_versionsPinnedForCommit.Count == 0) return;

        foreach (var (stream, pinned) in _versionsPinnedForCommit)
        {
            // A stream replaced or ejected since the call is no longer part of this commit
            if (!_streams.Values.Contains(stream)) continue;

            var current = stream.ActionType == StreamActionType.Start ? 0 : stream.ExpectedVersionOnServer;
            if (current != pinned)
            {
                throw new InvalidOperationException(
                    $"The expected version of stream '{(object?)stream.Key ?? stream.Id}' changed from {pinned} to "
                    + $"{current?.ToString() ?? "none"} after ProjectLatestForCommit(), by fetching it for writing "
                    + "again or appending with a new expected version, so the aggregate it returned no longer "
                    + "matches this commit. Call ProjectLatestForCommit() again after the last change to the stream.");
            }
        }

        _versionsPinnedForCommit.Clear();
    }
}
