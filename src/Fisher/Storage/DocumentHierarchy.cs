namespace Fisher.Storage;

/// <summary>
///     The <c>doc_type</c> discriminator column and the filter narrowing a hierarchy's table to one
///     sub-class (fisher#17).
/// </summary>
/// <remarks>
///     One place owns the column name and every fragment naming it, as <see cref="SoftDelete" /> and
///     <see cref="NumericRevision" /> do — the table definition, the descriptor and the storage layer
///     would otherwise each spell it out and have to agree.
/// </remarks>
internal static class DocumentHierarchy
{
    /// <summary>The column holding a row's sub-class alias.</summary>
    internal const string DocTypeColumn = "doc_type";

    /// <summary>
    ///     The filter matching a sub-class and everything registered below it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An <c>in</c> rather than an equality, because a sub-class may itself have sub-classes and
    ///         a query for the middle of a hierarchy has to return those too. Polecat emits a bare
    ///         equality, which is correct only for a two-level hierarchy.
    ///     </para>
    ///     <para>
    ///         The aliases are Fisher's own identifiers rather than caller input, so they are inlined as
    ///         literals instead of bound — which is what lets this be a fragment the query layer can
    ///         compose without owning a parameter slot, the same reasoning the soft-delete fragments
    ///         use. Quotes are still doubled, because an alias can be supplied by a caller.
    ///     </para>
    /// </remarks>
    internal static string FilterSqlFor(DocumentMapping mapping, Type queryType)
    {
        var aliases = new List<string> { mapping.AliasFor(queryType) };

        aliases.AddRange(mapping.SubClasses
            .Where(x => x.DocumentType != queryType && queryType.IsAssignableFrom(x.DocumentType))
            .Select(x => x.Alias));

        var quoted = aliases.Select(x => $"'{x.Replace("'", "''")}'").ToArray();

        return quoted.Length == 1
            ? $"{DocTypeColumn} = {quoted[0]}"
            : $"{DocTypeColumn} in ({string.Join(", ", quoted)})";
    }
}
