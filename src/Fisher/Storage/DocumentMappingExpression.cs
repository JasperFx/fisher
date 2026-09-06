using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Fisher.Storage.Metadata;
using Weasel.Core;

namespace Fisher.Storage;

/// <summary>
///     Configuration for one document type, typed so a member can be named with a lambda.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="DocumentMapping" /> itself is deliberately non-generic — it is reached from the
///         storage, the schema and the LINQ layer by <see cref="Type" />, none of which has a type
///         parameter to spare. But <c>Duplicate(x =&gt; x.Name)</c> cannot infer its document type from
///         a lambda alone, so the receiver has to carry it. Marten resolves this the same way, with
///         <c>MartenRegistry.DocumentMappingExpression&lt;T&gt;</c>.
///     </para>
///     <para>
///         Everything not needing the type parameter stays on <see cref="Mapping" />, which is public,
///         rather than being mirrored here — one place to configure a thing, and it is the mapping.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
    Justification =
        "Class-level: resolves the members named by a caller's own lambda. The document type and the members reached through it are preserved at the registration boundary on the caller side.")]
public class DocumentMappingExpression<T> where T : notnull
{
    internal DocumentMappingExpression(DocumentMapping mapping)
    {
        Mapping = mapping;
    }

    /// <summary>The mapping this configures.</summary>
    public DocumentMapping Mapping { get; }

    /// <summary>
    ///     Flag this document type for soft deletion — <c>Delete</c> sets <c>is_deleted</c> rather than
    ///     removing the row.
    /// </summary>
    public DocumentMappingExpression<T> SoftDeleted()
    {
        Mapping.SoftDeleted();
        return this;
    }

    /// <summary>
    ///     Guard writes of this type with an optimistic concurrency check against its
    ///     <c>guid_version</c> column.
    /// </summary>
    public DocumentMappingExpression<T> UseOptimisticConcurrency(bool enabled = true)
    {
        Mapping.UseOptimisticConcurrency = enabled;
        return this;
    }

    /// <summary>
    ///     Guard writes of this type with a numeric revision instead of a Guid version.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The alternative to <see cref="UseOptimisticConcurrency" />, not an addition to it — a
    ///         document type carries <c>guid_version</c> or <c>revision</c>, and asking for both is
    ///         refused when the type's storage is built.
    ///     </para>
    ///     <para>
    ///         What a revision buys over a Guid version is that it is readable: it can cross an API
    ///         boundary, be shown to a user, and be named on the way back in through
    ///         <c>Store(document, revision)</c> or <c>UpdateRevision(document, revision)</c>. Implementing
    ///         <see cref="JasperFx.IRevisioned" /> turns this on and maps the revision onto the
    ///         document's own <c>Version</c> member.
    ///     </para>
    /// </remarks>
    public DocumentMappingExpression<T> UseNumericRevisions(bool enabled = true)
    {
        Mapping.UseNumericRevisions = enabled;
        return this;
    }

    /// <summary>
    ///     Project Fisher's metadata columns onto members of the document — <c>last_modified</c> onto a
    ///     timestamp of your own, <c>deleted_at</c> onto something other than
    ///     <see cref="JasperFx.Metadata.ISoftDeleted.DeletedAt" />, and so on.
    /// </summary>
    /// <remarks>
    ///     Runs after the interface and attribute conventions and overrides them, because this is the
    ///     one of the three that is unambiguously a decision rather than an inference.
    /// </remarks>
    public DocumentMappingExpression<T> Metadata(Action<DocumentMetadataExpression<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        configure(new DocumentMetadataExpression<T>(Mapping.Metadata));
        return this;
    }

    /// <summary>
    ///     Give each row a tenant id, and make the primary key the tenant/id pair.
    /// </summary>
    public DocumentMappingExpression<T> MultiTenanted()
    {
        Mapping.TenancyStyle = JasperFx.MultiTenancy.TenancyStyle.Conjoined;
        return this;
    }

    /// <summary>
    ///     Override the short name the table is built from — <c>fi_doc_&lt;alias&gt;</c>.
    /// </summary>
    public DocumentMappingExpression<T> DocumentAlias(string alias)
    {
        Mapping.Alias = alias;
        return this;
    }

    /// <summary>
    ///     Lift a member out of the JSON body into a column of its own, and index it, so that queries
    ///     comparing against it can use that index.
    /// </summary>
    /// <param name="member">The member, e.g. <c>x =&gt; x.Name</c> or <c>x =&gt; x.Address.City</c>.</param>
    /// <param name="columnName">
    ///     An explicit column name. Defaults to the member chain lowercased and joined with
    ///     underscores.
    /// </param>
    /// <param name="columnType">
    ///     An explicit SQLite type. Defaults to one derived from the member's CLR type — see
    ///     <see cref="DuplicatedField.SqliteTypeFor" /> for why the default matters more than it looks.
    /// </param>
    /// <param name="index">Whether to create an index over the column. On by default; that is the point.</param>
    /// <remarks>
    ///     The column is a <c>VIRTUAL</c> generated column computed from <c>data</c>, so this can be
    ///     added to a document type that already has rows and every one of them is correct at once —
    ///     there is nothing to backfill. See <see cref="DuplicatedField" /> for the rest of that
    ///     decision.
    /// </remarks>
    public DocumentMappingExpression<T> Duplicate<TValue>(Expression<Func<T, TValue>> member,
        string? columnName = null, string? columnType = null, bool index = true)
    {
        ArgumentNullException.ThrowIfNull(member);

        Mapping.Duplicate(ChainOf(member), columnName, columnType, index);
        return this;
    }

    /// <summary>
    ///     Index a member where it lives, without lifting it into a column.
    /// </summary>
    /// <param name="member">The member, e.g. <c>x =&gt; x.Name</c> or <c>x =&gt; x.Address.City</c>.</param>
    /// <param name="name">An explicit index name. Defaults to one derived from the member chain.</param>
    /// <param name="unique">Whether to create a <c>UNIQUE</c> index.</param>
    /// <remarks>
    ///     <para>
    ///         A SQLite expression index over the member's <c>json_extract</c> locator — so unlike
    ///         <see cref="Duplicate{TValue}" /> this adds no column and does not change the table's
    ///         shape. Prefer it when all you want is the index; prefer <c>Duplicate</c> when the member
    ///         should also be a column something else can name.
    ///     </para>
    ///     <para>
    ///         Indexing a member that is <em>also</em> duplicated indexes the generated column rather
    ///         than the expression, because that is what a query against it emits — but a duplicated
    ///         field is indexed by default already, so saying both is usually redundant.
    ///     </para>
    /// </remarks>
    public DocumentMappingExpression<T> Index<TValue>(Expression<Func<T, TValue>> member,
        string? name = null, bool unique = false, Expression<Func<T, bool>>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(member);

        Mapping.Index([ChainOf(member)], name, unique, RenderPredicate(predicate));
        return this;
    }

    /// <summary>
    ///     Index several members together, as one composite index in the order given.
    /// </summary>
    /// <remarks>
    ///     Order matters exactly as it does for any B-tree index: SQLite can use a leading subset of the
    ///     indexed expressions and not a trailing one.
    /// </remarks>
    public DocumentMappingExpression<T> Index(Expression<Func<T, object?>>[] members,
        string? name = null, bool unique = false, Expression<Func<T, bool>>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(members);

        if (members.Length == 0)
        {
            throw new ArgumentException("An index needs at least one member.", nameof(members));
        }

        Mapping.Index(Array.ConvertAll(members, ChainOf), name, unique, RenderPredicate(predicate));
        return this;
    }

    /// <summary>
    ///     Declare this document type's full-text index, over SQLite's FTS5 (fisher#215).
    /// </summary>
    /// <param name="members">
    ///     The members to index. Naming none indexes the whole stored document, which is what Marten's
    ///     member-less <c>FullTextIndex()</c> does too — and note that it makes the JSON's <em>key
    ///     names</em> matchable terms as well as its values.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         The index is an external-content FTS5 virtual table kept in step by triggers, and it
    ///         goes through the ordinary schema migration — so <c>AutoCreate.None</c>, <c>db-apply</c>,
    ///         <c>db-assert</c> and <c>db-patch</c> all mean what they mean everywhere else. Declaring
    ///         it on a store that already holds documents populates it as part of creating it; see
    ///         <see cref="FullText.Fts5Table" />.
    ///     </para>
    ///     <para>
    ///         <b>One per document type.</b> A search operator names no index, so a second would have
    ///         nothing to tell it apart — put every searchable member in the one declaration.
    ///     </para>
    /// </remarks>
    public DocumentMappingExpression<T> FullTextIndex(params Expression<Func<T, object?>>[] members)
        => FullTextIndex(FullText.FullTextTokenizer.Porter, members);

    /// <summary>
    ///     Declare the full-text index with an explicit tokenizer.
    /// </summary>
    /// <param name="tokenizer">
    ///     How the index breaks text into terms. This decides which search operators can match against
    ///     it at all — <c>NgramSearch</c> requires <see cref="FullText.FullTextTokenizer.Trigram" />
    ///     and the word-oriented operators refuse one, each by name rather than by returning nothing.
    /// </param>
    /// <param name="members">The members to index, or none for the whole stored document.</param>
    public DocumentMappingExpression<T> FullTextIndex(FullText.FullTextTokenizer tokenizer,
        params Expression<Func<T, object?>>[] members)
    {
        ArgumentNullException.ThrowIfNull(members);

        Mapping.AddFullTextIndex(Array.ConvertAll(members, ChainOf), tokenizer);
        return this;
    }

    /// <summary>
    ///     Index a member and require its values to be distinct.
    /// </summary>
    /// <remarks>
    ///     A <c>UNIQUE</c> index over a member that is absent from some documents does not constrain
    ///     those: <c>json_extract</c> yields SQL NULL for a missing key, and SQLite treats NULLs as
    ///     distinct from one another in a unique index. So this constrains the documents that have the
    ///     member, which is what the equivalent does on both siblings.
    /// </remarks>
    public DocumentMappingExpression<T> UniqueIndex<TValue>(Expression<Func<T, TValue>> member,
        string? name = null, Expression<Func<T, bool>>? predicate = null)
        => Index(member, name, unique: true, predicate);

    /// <inheritdoc cref="UniqueIndex{TValue}(Expression{Func{T, TValue}}, string?, Expression{Func{T, bool}}?)" />
    public DocumentMappingExpression<T> UniqueIndex(Expression<Func<T, object?>>[] members,
        string? name = null, Expression<Func<T, bool>>? predicate = null)
        => Index(members, name, unique: true, predicate);

    /// <summary>
    ///     Declare a real foreign key from a member of <typeparamref name="T" /> to
    ///     <typeparamref name="TReference" />'s identity (fisher#38).
    /// </summary>
    /// <param name="member">The member holding the other document's identity.</param>
    /// <param name="onDelete">
    ///     What happens to these rows when the referenced document is deleted. Defaults to
    ///     <see cref="CascadeAction.NoAction" />, which refuses the delete while a child row points at
    ///     it.
    /// </param>
    /// <param name="columnName">
    ///     The generated column's name. Defaults to the member in snake case, as
    ///     <see cref="Duplicate{TValue}" /> does — and it is the same column, because this duplicates
    ///     the member.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b>Enforced from the moment it is declared.</b> SQLite's foreign key enforcement is
    ///         per-connection and off by default in the library, but Weasel's default pragma profile
    ///         turns it on for every connection Fisher opens.
    ///     </para>
    ///     <para>
    ///         <b>This duplicates the member as well</b>, because a foreign key needs a real column and
    ///         a document member lives in <c>data</c>. That column is a <c>VIRTUAL</c> generated one, so
    ///         it costs index space rather than row space and cannot drift — see
    ///         <see cref="DocumentForeignKey" /> for the verification that SQLite accepts a generated
    ///         column as a foreign key child at all, which was the one thing that could have sunk this.
    ///     </para>
    ///     <para>
    ///         <b>A document whose member is absent or null is unconstrained</b>, because
    ///         <c>json_extract</c> yields SQL NULL and SQLite exempts a NULL child value — the same
    ///         asymmetry <see cref="UniqueIndex{TValue}" /> documents, and the same on both siblings.
    ///     </para>
    /// </remarks>
    public DocumentMappingExpression<T> ForeignKey<TReference>(Expression<Func<T, object?>> member,
        CascadeAction onDelete = CascadeAction.NoAction, string? columnName = null)
        where TReference : notnull
    {
        ArgumentNullException.ThrowIfNull(member);

        Mapping.ForeignKey(ChainOf(member), typeof(TReference), onDelete, columnName);
        return this;
    }

    /// <summary>
    ///     Register a sub-class, so <typeparamref name="T" /> and its sub-classes share one table.
    /// </summary>
    /// <param name="alias">
    ///     The value stored in <c>doc_type</c>. Defaults to the type name in snake case; name it
    ///     explicitly if the type may be renamed, because the alias is what is stored.
    /// </param>
    /// <remarks>
    ///     <c>Store(derived)</c> and <c>LoadAsync&lt;TBase&gt;(id)</c> then share a table,
    ///     <c>Query&lt;TBase&gt;()</c> returns every sub-class as its own type, and
    ///     <c>Query&lt;TDerived&gt;()</c> narrows to one. An abstract or interface base is a hierarchy
    ///     whether or not anything is registered, so its table carries the discriminator from the first
    ///     migration.
    /// </remarks>
    public DocumentMappingExpression<T> AddSubClass<TSub>(string? alias = null) where TSub : T
    {
        Mapping.AddSubClass(typeof(TSub), alias);
        return this;
    }

    /// <inheritdoc cref="AddSubClass{TSub}" />
    public DocumentMappingExpression<T> AddSubClass(Type subclassType, string? alias = null)
    {
        ArgumentNullException.ThrowIfNull(subclassType);

        Mapping.AddSubClass(subclassType, alias);
        return this;
    }

    /// <summary>
    ///     Register every concrete sub-class of <typeparamref name="T" /> found in an assembly, so a
    ///     hierarchy of a dozen types is one line rather than twelve that have to be kept in sync with
    ///     the type tree (fisher#39).
    /// </summary>
    /// <param name="assembly">
    ///     Where to look. Defaults to the assembly declaring <typeparamref name="T" />, which is where
    ///     a hierarchy nearly always lives; name another when the sub-classes are elsewhere.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b>Abstract and interface types are skipped</b>, because a discriminator alias names
    ///         something a row can be deserialized as and nothing is ever stored as an abstract type.
    ///     </para>
    ///     <para>
    ///         <b>Ordered by full name, not by reflection order.</b> Two sub-classes whose default
    ///         aliases collide have to fail the same way on every run, and
    ///         <c>Assembly.GetTypes()</c> gives no ordering guarantee — an alias collision that showed
    ///         up on one machine and not another would be the worst possible version of that error.
    ///     </para>
    ///     <para>
    ///         Each type gets its default alias, which follows <c>DocumentMapping.Alias</c>'s convention
    ///         rather than snake case — see <see cref="AddSubClass{TSub}" />. A sub-class that needs a
    ///         stable alias across a rename should be named explicitly with <c>AddSubClass</c>, which
    ///         this leaves alone: registering the same type twice is idempotent.
    ///     </para>
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Scans a caller-named assembly for sub-classes of a document type the caller has already preserved.")]
    public DocumentMappingExpression<T> AddSubClassHierarchy(System.Reflection.Assembly? assembly = null)
    {
        var subclasses = (assembly ?? typeof(T).Assembly)
            .GetTypes()
            .Where(x => x != typeof(T) && typeof(T).IsAssignableFrom(x))
            .Where(x => !x.IsAbstract && !x.IsInterface)
            .OrderBy(x => x.FullName, StringComparer.Ordinal);

        foreach (var subclass in subclasses)
        {
            Mapping.AddSubClass(subclass, alias: null);
        }

        return this;
    }

    /// <summary>
    ///     Index the <c>last_modified</c> column (fisher#218).
    /// </summary>
    /// <remarks>
    ///     A plain column index, not an expression one: <c>last_modified</c> is a real column on every
    ///     document table, written on every upsert. It is what <c>ModifiedSince</c> /
    ///     <c>ModifiedBefore</c> compare against, and it holds <c>SqliteTimestamp</c>'s fixed-width UTC
    ///     form so a B-tree over it orders as instants.
    /// </remarks>
    public DocumentMappingExpression<T> IndexLastModified(string? name = null)
    {
        Mapping.ColumnIndex([Mapping.Metadata.LastModified.Name], name, isUnique: false);
        return this;
    }

    /// <summary>
    ///     Index the <c>created_at</c> column (fisher#218).
    /// </summary>
    /// <remarks>
    ///     <b>Enables the column as well as indexing it</b>, because <c>created_at</c> is opt-in and an
    ///     index over a column that does not exist is not a weaker version of this — it fails the
    ///     migration. Mapping a metadata member already enables its column for the same reason.
    /// </remarks>
    public DocumentMappingExpression<T> IndexCreatedAt(string? name = null)
    {
        Mapping.Metadata.CreatedAt.Enable();
        Mapping.ColumnIndex([Mapping.Metadata.CreatedAt.Name], name, isUnique: false);
        return this;
    }

    /// <summary>
    ///     Index the <c>tenant_id</c> column (fisher#218).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Refused for a type that is not <see cref="MultiTenanted" />, because there is no column
    ///         to index and silently doing nothing would look like it worked — the same rule the
    ///         tenancy query operators follow.
    ///     </para>
    ///     <para>
    ///         Worth less here than on either sibling and offered for parity: <c>tenant_id</c> already
    ///         <em>leads</em> the conjoined primary key, so the implicit tenant filter is served by that
    ///         index already. It earns its place for a query that filters on tenant and orders on
    ///         nothing the primary key covers.
    ///     </para>
    /// </remarks>
    public DocumentMappingExpression<T> IndexTenantId(string? name = null)
    {
        if (Mapping.TenancyStyle != JasperFx.MultiTenancy.TenancyStyle.Conjoined)
        {
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' is not MultiTenanted(), so its table has no tenant_id column to "
                + "index. Call MultiTenanted() first, or drop the IndexTenantId() call.");
        }

        Mapping.ColumnIndex([Mapping.Metadata.TenantId.Name], name, isUnique: false);
        return this;
    }

    /// <summary>
    ///     Soft delete this type, and index the live rows (fisher#218).
    /// </summary>
    /// <remarks>
    ///     Marten's <c>SoftDeletedWithIndex</c>. A <b>partial</b> index over <c>is_deleted</c>
    ///     restricted to the live rows, which is the shape that actually helps: every ordinary read
    ///     carries <c>is_deleted = 0</c>, and an index holding only those rows is the size of the live
    ///     set rather than of the table's whole history.
    /// </remarks>
    public DocumentMappingExpression<T> SoftDeletedWithIndex(string? name = null)
    {
        SoftDeleted();

        Mapping.ColumnIndex([SoftDelete.IsDeletedColumn], name, isUnique: false,
            predicate: $"{SoftDelete.IsDeletedColumn} = 0");

        return this;
    }

    /// <summary>
    ///     Leave an index alone that Fisher did not create (fisher#218).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         For an index added out of band — by hand, by a DBA, by a migration tool. Without this
    ///         the schema comparison sees an index the configuration does not declare and reports it as
    ///         surplus, so <c>db-assert</c> fails and <c>db-apply</c> drops it.
    ///     </para>
    ///     <para>
    ///         Ignoring a name Fisher itself declares is refused by Weasel rather than silently
    ///         preferring one of the two — that is a collision, not an exemption.
    ///     </para>
    /// </remarks>
    public DocumentMappingExpression<T> IgnoreIndex(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        Mapping.IgnoredIndexes.Add(indexName);
        return this;
    }

    /// <summary>
    ///     Name the identity member explicitly — <c>Identity(x =&gt; x.Key)</c> (fisher#218).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The DSL form of JasperFx's <c>[Identity]</c> attribute, for a type whose identity member
    ///         is not called <c>Id</c> and which you would rather not annotate — a type from another
    ///         assembly, or one shared with code that should not know about Fisher.
    ///     </para>
    ///     <para>
    ///         The mapping is created lazily on first use and the storage provider is built from it
    ///         later still, so this has to run during configuration. That is the same rule every other
    ///         member here follows; what makes it worth saying is that identity is the one thing
    ///         resolved in the mapping's own constructor.
    ///     </para>
    /// </remarks>
    public DocumentMappingExpression<T> Identity<TValue>(Expression<Func<T, TValue>> member)
    {
        ArgumentNullException.ThrowIfNull(member);

        var chain = ChainOf(member);

        if (chain.Length != 1)
        {
            throw new ArgumentException(
                $"'{member}' is not a member of {typeof(T).Name} itself. A document's identity has to "
                + "be one of its own members, not one reached through another.", nameof(member));
        }

        Mapping.UseIdentityMember(chain[0]);
        return this;
    }

    /// <summary>
    ///     Supply the identity strategy instead of letting Fisher derive one from the id's type
    ///     (fisher#218).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Fisher otherwise picks by <c>IdType</c>: a version-7 Guid, an externally-assigned
    ///         string, a Hi-Lo <c>int</c> or <c>long</c>, or the unwrapping strategy for a strong-typed
    ///         wrapper. This is the seam for anything else — a ULID in a string key, a snowflake
    ///         <c>long</c>, a tenant-prefixed key.
    ///     </para>
    ///     <para>
    ///         <b>Marten's <c>IdStrategy(IIdGeneration)</c> has no direct counterpart and this is the
    ///         honest translation.</b> That type is a code-generation contract; Fisher's strategies are
    ///         ordinary objects from the shared Weasel identity runtime, so the seam is that runtime
    ///         interface — two members to implement rather than a generator to write.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>A Guid strategy is wrapped so the id still crosses the ADO.NET boundary as
    ///         lowercase canonical text.</b> Binding a raw <see cref="Guid" /> writes the UPPERCASE
    ///         form, SQLite's default collation is case-sensitive, and the result is rows that can
    ///         never be read back — every load null, every id match empty, silently. That conversion
    ///         lives in the identity strategy, so a caller-supplied one is exactly where it could have
    ///         been lost; <c>DocumentProviderRegistry</c> puts it back rather than leaving the trap
    ///         open.
    ///     </para>
    /// </remarks>
    public DocumentMappingExpression<T> IdStrategy<TId>(
        Weasel.Core.Identity.IIdentification<T, TId> strategy) where TId : notnull
    {
        ArgumentNullException.ThrowIfNull(strategy);

        if (!Mapping.HasIdentity)
        {
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' has no identity member for a strategy to assign to. Name one with "
                + "Identity(x => x.Member) before IdStrategy(...), or mark it with [Identity].");
        }

        if (typeof(TId) != Mapping.IdType)
        {
            throw new ArgumentException(
                $"'{typeof(T).Name}' is identified by '{Mapping.IdType.Name}', so its strategy has to "
                + $"be an IIdentification<{typeof(T).Name}, {Mapping.IdType.Name}>. Call Identity(...) "
                + "first if the identity member itself is not the one Fisher resolved.",
                nameof(strategy));
        }

        Mapping.IdStrategy = strategy;
        return this;
    }

    /// <summary>
    ///     Configure the Hi-Lo sequence backing an <c>int</c> or <c>long</c> identity (fisher#218).
    /// </summary>
    /// <remarks>
    ///     The method form of <c>Mapping.HiloSettings</c>, so a block of configuration reads the same
    ///     as Marten's. <c>SequenceName</c> is what makes two document types share one allocation
    ///     rather than each holding a private lo range over the same row; <c>MaxLo</c> trades how many
    ///     identities are lost on an unclean shutdown against how often the sequence is advanced.
    ///     Ignored for a Guid or string identity, which needs no sequence.
    /// </remarks>
    public DocumentMappingExpression<T> HiloSettings(Weasel.Core.Sequences.HiloSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Mapping.HiloSettings = settings;
        return this;
    }

    /// <summary>
    ///     Render an index's partial predicate to literal SQL.
    /// </summary>
    /// <remarks>
    ///     Through the same <c>WhereClauseParser</c> and <c>MemberFactory</c> a query goes through,
    ///     which is what makes the index usable: SQLite reaches a partial index only when the query's
    ///     <c>WHERE</c> implies the index's, over the terms as written. See
    ///     <c>LiteralRenderingCommandBuilder</c> for why the values are rendered rather than bound and
    ///     what that costs.
    /// </remarks>
    private string? RenderPredicate(Expression<Func<T, bool>>? predicate)
    {
        if (predicate is null)
        {
            return null;
        }

        var members = new Linq.Members.MemberFactory(Mapping.StoreOptions, Mapping);
        var fragment = new Linq.Parsing.WhereClauseParser(members).Parse(predicate.Body);

        var builder = new Linq.SqlGeneration.LiteralRenderingCommandBuilder();
        fragment.Apply(builder);

        return builder.ToString();
    }

    /// <summary>
    ///     Walk a member-access lambda back to its parameter, outermost member last.
    /// </summary>
    /// <remarks>
    ///     A value-typed member arrives wrapped in a <c>Convert</c> when the lambda's return type is
    ///     wider than the member's, which is why the boxing conversion is stripped before the walk.
    /// </remarks>
    private static MemberInfo[] ChainOf<TValue>(Expression<Func<T, TValue>> expression)
    {
        var body = expression.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member)
        {
            throw new ArgumentException(
                $"'{expression}' is not a member of {typeof(T).Name}. Duplicate a property or field, "
                + "e.g. x => x.Name.", nameof(expression));
        }

        var chain = new List<MemberInfo>();
        MemberExpression? current = member;

        while (current is not null)
        {
            chain.Insert(0, current.Member);

            if (current.Expression is ParameterExpression)
            {
                return chain.ToArray();
            }

            current = current.Expression as MemberExpression;
        }

        throw new ArgumentException(
            $"'{expression}' does not resolve to a member of the document itself.", nameof(expression));
    }
}
