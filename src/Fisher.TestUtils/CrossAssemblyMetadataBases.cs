namespace Fisher.TestUtils;

// fisher#677. Base classes for Fisher.Tests' inherited_metadata_members, kept in this assembly on purpose. A
// derived class in ANOTHER assembly that implements IRevisioned (or IVersioned, ISoftDeleted, ITracked) over one
// of these non-virtual properties cannot use the base setter as the interface implementation, so the compiler
// emits a hidden private forwarding method (JasperFx.IRevisioned.set_Version) with no property behind it. That is
// Wolverine's Saga.Version shape (wolverine#5658). Declared in the same assembly as the derived class, the compiler
// marks the base accessors virtual final instead and the interface map points straight at them.

public abstract class CrossAssemblyRevisionedBase
{
    public int Version { get; set; }
}

public abstract class CrossAssemblyVersionedBase
{
    public Guid Version { get; set; }
}

public abstract class CrossAssemblySoftDeletedBase
{
    public bool Deleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public abstract class CrossAssemblyTrackedBase
{
    public string? CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public string? LastModifiedBy { get; set; }
}
