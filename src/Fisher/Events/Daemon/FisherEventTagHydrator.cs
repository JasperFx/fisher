using System.Globalization;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Microsoft.Data.Sqlite;

namespace Fisher.Events.Daemon;

/// <summary>
///     Reads the stored DCB tags of a page of events back onto them (fisher#652, marten#6003), for a projection that
///     groups by tag (<c>IdentityFromTag&lt;TTag&gt;()</c>, which sets <c>EventFilterable.IncludeEventTags</c>).
/// </summary>
/// <remarks>
///     <para>
///         The daemon's loader skips tags for every other projection, because nothing reads them and each tag type is
///         one more query per page. When asked, it is one query per registered tag type over the page's sequence range,
///         served by each tag table's <c>seq_id</c> index.
///     </para>
///     <para>
///         The tag table holds the tag's simple value (a Guid as lowercase text, numbers as INTEGER). It is wrapped
///         back into the registered tag type, which is what the slicer compares.
///     </para>
/// </remarks>
internal static class FisherEventTagHydrator
{
    internal static async Task HydrateAsync(EventGraph events, SqliteConnection connection,
        IReadOnlyList<IEvent> page, int commandTimeout, CancellationToken token)
    {
        if (page.Count == 0 || events.TagTypes.Count == 0) return;

        var bySequence = new Dictionary<long, IEvent>(page.Count);
        var min = long.MaxValue;
        var max = long.MinValue;
        foreach (var @event in page)
        {
            bySequence[@event.Sequence] = @event;
            min = Math.Min(min, @event.Sequence);
            max = Math.Max(max, @event.Sequence);
        }

        foreach (var registration in events.TagTypes)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = commandTimeout;
            command.CommandText =
                $"select seq_id, value from {events.TagTableName(registration)} where seq_id >= @min and seq_id <= @max";
            command.Parameters.AddWithValue("@min", min);
            command.Parameters.AddWithValue("@max", max);

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (!bySequence.TryGetValue(reader.GetInt64(0), out var @event)) continue;

                var value = TagInstance(registration, reader.GetValue(1));
                if (@event.Tags is { } existing && existing.Any(x => x.TagType == registration.TagType &&
                                                                      Equals(x.Value, value)))
                {
                    continue;
                }

                @event.AddTag(new EventTag(registration.TagType, value));
            }
        }
    }

    /// <summary>A stored tag value back as an instance of the registered tag type.</summary>
    internal static object TagInstance(ITagTypeRegistration registration, object stored)
    {
        var simpleType = registration.SimpleType;

        object simple = simpleType == typeof(Guid) ? Guid.Parse((string)stored)
            : simpleType == typeof(string) ? Convert.ToString(stored, CultureInfo.InvariantCulture)!
            : Convert.ChangeType(stored, simpleType, CultureInfo.InvariantCulture);

        if (registration.TagType == simpleType) return simple;

        return StrongTypedIdResolver.TryResolve(registration.TagType, out var info, StrongTypedIdStyle.AnyType)
            ? StrongTypedIdResolver.Wrap(info, simple)
            : throw new InvalidOperationException(
                $"Tag type {registration.TagType.FullNameInCode()} wraps {simpleType.Name}, but Fisher can't build one from a stored value.");
    }
}
