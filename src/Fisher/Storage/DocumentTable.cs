using JasperFx;
using Weasel.Sqlite.Tables;

namespace Fisher.Storage;

/// <summary>
///     Weasel table definition for one document type — <c>fi_doc_&lt;alias&gt;</c>.
/// </summary>
/// <remarks>
///     <para>
///         The column set is intentionally small. Marten and Polecat carry more here (duplicated
///         fields, hierarchy discriminators, partition keys); each of those is additive and can arrive
///         without changing the columns below, as soft delete's two did.
///     </para>
///     <para>
///         <c>tenant_id</c> exists only on conjoined mappings, matching Polecat. A single-tenant table
///         has nothing to filter by, and an always-present column would put a redundant value on every
///         row and a redundant predicate in every query.
///     </para>
/// </remarks>
internal class DocumentTable : Table
{
    public DocumentTable(DocumentMapping mapping) : base(mapping.TableName)
    {
        // The identity column carries the primary key on its own for a single-tenant table. Under
        // conjoined tenancy the same id may exist once per tenant, so the key is the pair — which is
        // also why the tenant column is added before any other, keeping the composite key's leading
        // column the one every query filters on.
        if (mapping.IsConjoined)
        {
            AddColumn(StorageConstants.TenantIdColumn, "TEXT")
                .NotNull()
                .DefaultValueByString(StorageConstants.DefaultTenantId)
                .AsPrimaryKey();
        }

        AddColumn("id", mapping.IdColumnType).NotNull().AsPrimaryKey();

        // The document body. SQLite's json1 functions read TEXT directly, so there is no jsonb
        // equivalent to reach for.
        AddColumn("data", "TEXT").NotNull();

        // Optimistic concurrency rides its own column rather than the document body, so a version
        // check never has to parse JSON.
        if (mapping.UseOptimisticConcurrency)
        {
            AddColumn("guid_version", "TEXT").NotNull();
        }

        // The numeric alternative, and mutually exclusive with the Guid one — DocumentMapping refuses
        // the pair, so at most one of these two columns is ever created. INTEGER because the revision
        // is compared and incremented as a number; a TEXT affinity here would make revision 10 sort
        // below revision 9 and turn the "must be greater" guard into nonsense.
        if (mapping.UseNumericRevisions)
        {
            AddColumn(NumericRevision.Column, "INTEGER").NotNull().DefaultValue(1);
        }

        // The concrete .NET type the row was written as, assembly-qualified. Written on every save and
        // never selected — the hierarchy discriminator is doc_type below, deliberately a separate
        // column holding a short alias. See SubClassMapping for why this one is not it.
        AddColumn("dotnet_type", "TEXT").AllowNulls();

        // The hierarchy discriminator: a short alias, read on every load so a row can be deserialized
        // as its own sub-class. Indexed, because narrowing a query to one sub-class is a predicate on
        // this column and is most of the point of registering the hierarchy.
        if (mapping.IsHierarchy)
        {
            AddColumn(DocumentHierarchy.DocTypeColumn, "TEXT").AllowNulls().AddIndex();
        }

        // ISO-8601 UTC, same representation and the same parenthesized-expression trap as the event
        // tables: a non-literal DEFAULT must be wrapped in parentheses or CREATE TABLE will not parse.
        AddColumn("last_modified", "TEXT")
            .NotNull()
            .DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);

        // Soft delete adds two columns and nothing else. is_deleted is INTEGER 0/1 rather than a
        // boolean and carries a DEFAULT so a row written by anything that predates the flag still
        // reads as live; deleted_at is nullable because a live row has no deletion time. Only the
        // soft-delete operation writes a concrete timestamp there — every ordinary write clears it.
        if (mapping.IsSoftDeleted)
        {
            AddColumn(SoftDelete.IsDeletedColumn, "INTEGER").NotNull().DefaultValue(0);
            AddColumn(SoftDelete.DeletedAtColumn, "TEXT").AllowNulls();
        }

        AddOptionalMetadata(mapping);
        AddDuplicatedFields(mapping);
        AddDeclaredIndexes(mapping);
        ApplyIgnoredIndexes(mapping);
        AddForeignKeys(mapping);
    }

    /// <summary>
    ///     A table-level <c>foreign key (…) references &lt;table&gt; (id)</c> per declared key
    ///     (fisher#38).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The child column is the duplicated field the declaration created, which is a
    ///         <c>VIRTUAL</c> generated column — accepted by SQLite as a foreign key child, and
    ///         enforced. See <see cref="DocumentForeignKey" /> for the verification.
    ///     </para>
    ///     <para>
    ///         <b>The referenced table is named unqualified</b>, which is not a shortcut: SQLite's
    ///         <c>REFERENCES</c> clause takes a bare table name and cannot be schema-qualified, and
    ///         Fisher folds its logical schema into the table <em>prefix</em> rather than using real
    ///         schemas — so the name Weasel renders is already the whole name. Two logical stores in one
    ///         file therefore each get their own key to their own table.
    ///     </para>
    ///     <para>
    ///         Added after the columns because it names one, and rendered inline with the CREATE TABLE
    ///         because SQLite has no <c>ALTER TABLE ADD CONSTRAINT</c> — adding a key to a type that
    ///         already has a table means recreating it, which Weasel reports rather than attempting.
    ///     </para>
    /// </remarks>
    private void AddForeignKeys(DocumentMapping mapping)
    {
        foreach (var declared in mapping.ForeignKeys)
        {
            var field = mapping.DuplicateFor(declared.Members)
                        ?? throw new InvalidOperationException(
                            $"The foreign key on '{mapping.DocumentType.Name}."
                            + $"{string.Join(".", declared.MemberNames)}' has no column. Declaring one is "
                            + "supposed to duplicate the member; this is a bug in DocumentMapping.ForeignKey.");

            var referenced = mapping.StoreOptions.Schema.MappingFor(declared.ReferencedType);

            ForeignKeys.Add(new Weasel.Sqlite.Tables.ForeignKey(
                $"fkey_{Identifier.Name}_{field.ColumnName}")
            {
                ColumnNames = [field.ColumnName],
                LinkedTable = referenced.TableName,
                LinkedNames = ["id"],
                OnDelete = declared.OnDelete
            });
        }
    }

    /// <summary>
    ///     The columns fisher#29 added, each present only once something has asked for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Opt-in so that adding the feature does not widen a table nobody asked to widen — a
    ///         document type configured before this existed keeps exactly the columns it had.
    ///     </para>
    ///     <para>
    ///         <c>created_at</c> is the one with a DEFAULT, and it is the whole mechanism by which the
    ///         column is filled: no write binder contributes it, so it appears in no INSERT column list
    ///         and in no <c>do update set</c> clause, and an update therefore cannot move it. The
    ///         expression is <b>parenthesized</b> by <c>NowDefaultExpression</c> — a bare
    ///         <c>DEFAULT strftime(...)</c> is a CREATE TABLE syntax error.
    ///     </para>
    ///     <para>
    ///         <c>tenant_id</c> is absent here on purpose: <c>MultiTenanted()</c> creates it above, and
    ///         its metadata column only decides whether the value is read back onto a member.
    ///     </para>
    /// </remarks>
    private void AddOptionalMetadata(DocumentMapping mapping)
    {
        var metadata = mapping.Metadata;

        if (metadata.CreatedAt.Enabled)
        {
            AddColumn(metadata.CreatedAt.Name, "TEXT")
                .NotNull()
                .DefaultValueByExpression(SqliteTimestamp.NowDefaultExpression);
        }

        if (metadata.CorrelationId.Enabled)
        {
            AddColumn(metadata.CorrelationId.Name, "TEXT").AllowNulls();
        }

        if (metadata.CausationId.Enabled)
        {
            AddColumn(metadata.CausationId.Name, "TEXT").AllowNulls();
        }

        if (metadata.LastModifiedBy.Enabled)
        {
            AddColumn(metadata.LastModifiedBy.Name, "TEXT").AllowNulls();
        }

        // TEXT holding JSON, as data does — SQLite's json1 reads TEXT directly and there is no jsonb.
        if (metadata.Headers.Enabled)
        {
            AddColumn(metadata.Headers.Name, "TEXT").AllowNulls();
        }
    }

    /// <summary>
    ///     One SQLite expression index per user-declared index.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The indexed expression is the member's <c>TypedLocator</c>, from the same
    ///         <see cref="Linq.Members.MemberFactory" /> a query goes through — see
    ///         <see cref="DocumentIndex" /> for why building it here instead is the classic way to get an
    ///         index that is created, never used, and never wrong enough to notice.
    ///     </para>
    ///     <para>
    ///         A member that is also duplicated resolves to a <c>DuplicatedMember</c> whose
    ///         <c>TypedLocator</c> is the generated column's name, so the index lands on the column. That
    ///         is correct rather than special-cased: the locator is what the query emits either way.
    ///     </para>
    /// </remarks>
    private void AddDeclaredIndexes(DocumentMapping mapping)
    {
        if (mapping.Indexes.Count == 0)
        {
            return;
        }

        var members = new Linq.Members.MemberFactory(mapping.StoreOptions, mapping);

        foreach (var declared in mapping.Indexes)
        {
            var definition = new Weasel.Sqlite.Tables.IndexDefinition(
                declared.Name ?? DefaultIndexName(declared.DefaultNameSuffix()))
            {
                IsUnique = declared.IsUnique,

                // A partial index, already rendered: DDL carries no parameters, so the predicate was
                // written out at configuration time from the same parser a query goes through. That
                // matching is what makes the index reachable at all -- SQLite uses one only when the
                // query's WHERE implies the index's, over the terms as written.
                Predicate = declared.Predicate
            };

            if (declared.Columns.Length > 0)
            {
                // The metadata-column indexes. Real columns, so there is nothing to resolve through
                // the member factory and no expression to build.
                definition.Columns = declared.Columns;
            }
            else
            {
                // Several expressions render as one comma-separated list, which Weasel wraps in the
                // parentheses a composite index needs.
                definition.Expression = string.Join(", ",
                    declared.MemberChains.Select(chain => members.ResolveMember(chain).TypedLocator));
            }

            Indexes.Add(definition);
        }
    }

    /// <summary>
    ///     Names the schema comparison must leave alone — <c>IgnoreIndex</c> (fisher#218).
    /// </summary>
    /// <remarks>
    ///     After the declared indexes deliberately: Weasel refuses to ignore a name the table itself
    ///     declares, and that refusal is the point. Ignoring one of Fisher's own indexes is a collision
    ///     rather than an exemption, and it would otherwise resolve silently in whichever direction the
    ///     ordering happened to give.
    /// </remarks>
    private void ApplyIgnoredIndexes(DocumentMapping mapping)
    {
        foreach (var name in mapping.IgnoredIndexes)
        {
            IgnoreIndex(name);
        }
    }

    /// <summary>
    ///     <c>idx_&lt;table&gt;_&lt;members&gt;</c>, which is the shape Weasel gives the index it creates
    ///     for a duplicated column.
    /// </summary>
    /// <remarks>
    ///     Weasel.Sqlite's <c>DbObjectName.ToIndexName</c> is <see langword="internal" />, so the formula
    ///     is repeated rather than called. It is repeated on purpose: a user-declared index and a
    ///     duplicated-field index should be indistinguishable in <c>sqlite_master</c>, because which
    ///     mechanism created one is Fisher's business and not the reader's.
    /// </remarks>
    private string DefaultIndexName(string suffix) => $"idx_{Identifier.Name}_{suffix}";

    /// <summary>
    ///     A <c>VIRTUAL</c> generated column per duplicated field, and an index over it unless the
    ///     registration declined one.
    /// </summary>
    /// <remarks>
    ///     The expression is the member's own <c>TypedLocator</c>, taken from the same
    ///     <see cref="Linq.Members.MemberFactory" /> a query goes through — so the column holds exactly
    ///     what a predicate against that member looks for, including a timestamp's <c>strftime</c>
    ///     normalisation. Building it here instead would be the classic way to have an index that is
    ///     never used and never wrong enough to notice.
    /// </remarks>
    private void AddDuplicatedFields(DocumentMapping mapping)
    {
        if (mapping.DuplicatedFields.Count == 0)
        {
            return;
        }

        var members = new Linq.Members.MemberFactory(mapping.StoreOptions, mapping);

        foreach (var field in mapping.DuplicatedFields)
        {
            var member = members.ResolveMember(field.Members);

            // The resolved member is the duplicated one — its TypedLocator is the column being
            // declared. The expression has to be what that column replaces, so read it off the inner
            // JSON locator instead.
            var expression = member is Linq.Members.DuplicatedMember duplicated
                ? duplicated.GeneratedExpression
                : member.TypedLocator;

            var column = AddColumn(field.ColumnName, field.ColumnType ??
                                                     DuplicatedField.SqliteTypeFor(member.MemberType))
                .AllowNulls()
                .GeneratedAs(expression);

            if (field.ShouldIndex)
            {
                column.AddIndex();
            }
        }
    }
}
