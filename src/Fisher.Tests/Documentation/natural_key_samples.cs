using JasperFx.Events;
using JasperFx.Events.Aggregation;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/events/natural-keys.md's "A null from a key source" section (fisher#684,
 * jasperfx#1287). The behaviour is pinned by NaturalKeyCompliance's a_null_from_an_inferred_key_source_keeps_the_key
 * and a_null_from_an_explicit_key_source_removes_the_key, and by natural_keys.a_replay_does_not_restore_a_key_an_explicit_source_removed.
 */

#region sample_natural_key_sources_and_null

public record ShipmentBooked(string Code);

public record ShipmentUpdated(string? NewCode, string Notes);

public record ShipmentCodeWithdrawn;

public partial class Shipment
{
    public Guid Id { get; set; }

    [NaturalKey]
    public string? Code { get; set; }

    public string Notes { get; set; } = string.Empty;

    [NaturalKeySource]
    public void Apply(ShipmentBooked e) => Code = e.Code;

    // Inferred: the key is read off the event, so a null NewCode means "this event carries no key"
    // and the stream keeps its code
    [NaturalKeySource]
    public void Apply(ShipmentUpdated e)
    {
        if (e.NewCode != null) Code = e.NewCode;
        Notes = e.Notes;
    }

    // Explicit: a static method returning the key type, so null removes the stream's key and frees it
    [NaturalKeySource]
    public static string? KeyFor(ShipmentCodeWithdrawn e) => null;

    public void Apply(ShipmentCodeWithdrawn e) => Code = null;
}

#endregion
