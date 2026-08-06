namespace Fisher.Attributes;

/// <summary>
///     Marks a document type as soft-deleted: <c>Delete</c> flags the row rather than removing it, and
///     every read filters the flagged rows out.
/// </summary>
/// <remarks>
///     The other two ways to say the same thing are implementing
///     <see cref="JasperFx.Metadata.ISoftDeleted" /> and calling
///     <c>StoreOptions.Schema.For&lt;T&gt;().SoftDeleted()</c>. All three are read once, when the
///     document's mapping is created.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SoftDeletedAttribute : Attribute;
