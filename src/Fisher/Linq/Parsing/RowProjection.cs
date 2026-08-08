namespace Fisher.Linq.Parsing;

/// <summary>
///     The columns a projected query selects and the factory that rebuilds one result from them —
///     whichever kind of projection produced it.
/// </summary>
/// <remarks>
///     <see cref="SelectProjection" /> (a <c>Select</c> over documents) and
///     <see cref="GroupProjection" /> (a <c>Select</c> over an <c>IGrouping</c>) differ entirely in how
///     they are analysed and not at all in what the provider does with the result: read these columns,
///     coerce them, call this factory. This is that common shape, so the provider has one projected
///     read path rather than two that would drift.
/// </remarks>
internal sealed record RowProjection(string[] Columns, Type[] ColumnTypes, Func<object?[], object?> Build)
{
    /// <summary>
    ///     The projection this query carries, or null when it returns documents.
    /// </summary>
    /// <remarks>
    ///     A grouped query is always projected — <c>GroupBy</c> without a <c>Select</c> would have to
    ///     hand back <c>IGrouping</c> instances, which means materializing every row of every group and
    ///     defeats the point of grouping in SQL. Refused here rather than silently producing one row
    ///     per group with nothing in it.
    /// </remarks>
    public static RowProjection? For(LinqQueryParser parser)
    {
        if (parser.GroupByLocator is not null)
        {
            var grouped = parser.GroupProjection
                          ?? throw new BadLinqExpressionException(
                              "A GroupBy needs a Select describing what each group should produce — the "
                              + "key, an aggregate over the group, or both. Returning the groups "
                              + "themselves would mean reading every row of every one.");

            return new RowProjection(grouped.Columns, grouped.ColumnTypes, grouped.Build);
        }

        return parser.Projection is { } projection
            ? new RowProjection(projection.Locators, projection.ColumnTypes, projection.Build)
            : null;
    }
}
