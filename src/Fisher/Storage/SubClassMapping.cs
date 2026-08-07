namespace Fisher.Storage;

/// <summary>
///     One sub-class registered against a document hierarchy's base type, and the alias that stands
///     for it in the <c>doc_type</c> discriminator column (fisher#17).
/// </summary>
/// <remarks>
///     <para>
///         <b>The discriminator is a short alias in its own column, not <c>dotnet_type</c>.</b> That is
///         worth stating because <c>dotnet_type</c> is already written on every row and looks like the
///         obvious candidate — but it holds an assembly-qualified name, which is long, not worth
///         indexing, and brittle across an assembly rename. It is also written by Weasel's
///         <c>DocumentDotNetTypeBinder</c>, which takes no alias resolver; the binder built for exactly
///         this job is <c>DocumentDocTypeBinder</c>, and it does. Marten and Polecat keep the two
///         columns separate for the same reasons.
///     </para>
///     <para>
///         The alias defaults to the type name in snake case, matching every other Fisher-owned name.
///         Naming it explicitly is how a row survives a type rename, since the alias is what is stored.
///     </para>
/// </remarks>
internal sealed class SubClassMapping
{
    internal SubClassMapping(Type documentType, string alias)
    {
        DocumentType = documentType;
        Alias = alias;
    }

    internal Type DocumentType { get; }

    internal string Alias { get; }

    /// <summary>
    ///     <c>SuperUser</c> becomes <c>superuser</c>.
    /// </summary>
    /// <remarks>
    ///     The same convention <see cref="DocumentMapping.Alias" /> uses, deliberately, rather than the
    ///     snake case every Fisher-owned <em>column</em> name follows. The base type's discriminator
    ///     alias <em>is</em> its <c>Alias</c> — the one the table is named from — so a sub-class using a
    ///     different convention would put two spellings in one column, and a reader would have to know
    ///     which type produced a row to know which to expect.
    /// </remarks>
    internal static string DefaultAliasFor(Type documentType)
        => DocumentMapping.DefaultAliasFor(documentType);
}
