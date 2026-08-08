namespace Fisher.Pagination;

/// <summary>
///     A keyset page: the items, and the cursor that fetches the next one (fisher#27).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="NextCursor" /> is null when the page is the last one. Pass it back to
///         <c>ToCursorPageAsync</c> to continue; it is opaque and should be treated as such by callers,
///         though its format is deliberately Polecat's so a cursor is portable between the stores.
///     </para>
///     <para>
///         <b>Typed rather than JSON, which is where Fisher diverges from Polecat.</b> Polecat's
///         <c>CursorPageResult</c> carries pre-rendered JSON because it pairs with a
///         <c>StreamPagedByCursor</c> HTTP result in its ASP.NET Core package. Fisher has neither that
///         package (fisher#49) nor JSON-returning reads (fisher#28) yet, so a JSON-shaped result would
///         be a shape with no consumer. The JSON variant belongs with fisher#49, built on this.
///     </para>
/// </remarks>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
