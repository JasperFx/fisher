using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Fisher.Storage;
using JasperFx;
using JasperFx.Documents;
using Microsoft.Data.Sqlite;

namespace Fisher;

/// <summary>
///     <see cref="IDocumentStoreDiagnostics.ExplainDocumentQueryAsync" /> (jasperfx#1078): SQLite's
///     <c>EXPLAIN QUERY PLAN</c> over the page statement <c>QueryDocumentsAsync</c> would run for the same
///     arguments.
/// </summary>
/// <remarks>
///     <para>
///         <b>The same statement, not a re-derivation</b> — the rule fisher#202's
///         <c>ExplainAsync</c> already follows, for the same reason: SQLite reaches an expression index only
///         when the query's expression matches the index's, so a plan over anything but the real text can
///         report the wrong answer confidently. The hand-built read and the criteria read each share their
///         statement builder with the explain (<c>StoredRowsSql</c>/<c>PageTail</c>, and
///         <c>CriteriaRead.PageStatement</c>), and the refusals run in the same order as the query's.
///     </para>
///     <para>
///         <b>SQLite counts nothing</b>: its plan is prose, with no row estimates and no execution pass,
///         so <see cref="DocumentQueryPlan.RowsExamined" /> and <see cref="DocumentQueryPlan.RowsReturned" />
///         are null — "the engine does not say" — and <see cref="DocumentQueryPlan.IsActual" /> is false.
///         What it does say is which index each step reads through and whether a step scans the table,
///         and those are read off the prose: <c>SEARCH … USING [COVERING] INDEX name</c>, and a
///         <c>SCAN &lt;document table&gt;</c> row. A <c>SCAN … USING INDEX</c> (a full walk in index order,
///         which the default <c>order by id</c> produces) still reads every row, so it reports both.
///     </para>
///     <para>
///         <b>Nothing is executed</b>: <c>EXPLAIN QUERY PLAN</c> prepares the statement and describes it.
///     </para>
/// </remarks>
public partial class DocumentStore
{
    /// <remarks>
    ///     <para>
    ///         An unknown type is null, there being no query to explain. So is a type whose table does not
    ///         exist yet: <c>QueryDocumentsAsync</c> answers that case with an empty page and runs no
    ///         statement at all, and SQLite cannot prepare one against a missing table — and this read must
    ///         not create it, being a monitoring read. Null renders as "unknown", never as "no scan".
    ///     </para>
    ///     <para>
    ///         <see cref="DocumentQueryOptions.AllTenants" /> runs one page statement per (file, tenant)
    ///         segment, all of one shape; the plan is of the first segment's, in tenant order.
    ///     </para>
    /// </remarks>
    async Task<DocumentQueryPlan?> IDocumentStoreDiagnostics.ExplainDocumentQueryAsync(string documentTypeName,
        DocumentQueryOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AssertValidTenantScope();

        var pageNumber = Math.Max(1, options.PageNumber);
        var pageSize = Math.Max(1, options.PageSize);
        var skip = (long)(pageNumber - 1) * pageSize;

        var (mapping, queriedType) = ResolveForDiagnostics(documentTypeName);

        if (mapping is null || queriedType is null)
        {
            return null;
        }

        // QueryDocumentsAsync's refusals, in its order.
        AssertMetadataFiltersAreCaptured(mapping, queriedType, options);
        var metadataOrder = MetadataOrderFor(mapping, options);

        var target = await ExplainTargetAsync(mapping, options, token).ConfigureAwait(false);

        if (options.HasCriteria())
        {
            await using var read = await OpenCriteriaReadAsync(target.Database, target.TenantId, mapping,
                queriedType, options).ConfigureAwait(false);

            if (!target.TableExists)
            {
                return null;
            }

            var steps = await read.ExplainPageAsync(pageSize, skip, token).ConfigureAwait(false);
            return PlanFrom(mapping, steps);
        }

        if (!target.TableExists)
        {
            return null;
        }

        await using var connection = await target.Database.OpenConnectionAsync(token).ConfigureAwait(false);

        var (where, bind) = FilterFor(mapping, queriedType, target.TenantId, options).Build();

        await using var command = connection.CreateCommand();
        command.CommandText = "explain query plan "
                              + StoredRowsSql(new StoredRowLayout(mapping), mapping, where, PageTail(metadataOrder));
        bind(command);
        command.Parameters.AddWithValue("$take", pageSize);
        command.Parameters.AddWithValue("$skip", skip);

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return PlanFrom(mapping, await ReadExplainedStepsAsync(reader, token).ConfigureAwait(false));
    }

    /// <summary>
    ///     The file and tenant the page statement would run against, resolved as the query resolves them.
    /// </summary>
    private async Task<(FisherDatabase Database, string? TenantId, bool TableExists)> ExplainTargetAsync(
        DocumentMapping mapping, DocumentQueryOptions options, CancellationToken token)
    {
        if (!options.AllTenants)
        {
            var tenantId = DocumentQueryOptions.NormalizeTenantId(options.TenantId);
            var database = DatabaseForDiagnostics(tenantId);
            tenantId = tenantId is null ? null : Tenancy.TenantIdFor(tenantId); // fisher#393

            return (database, tenantId, await TableExistsAsync(database, mapping, token).ConfigureAwait(false));
        }

        IReadOnlyList<FisherDatabase> databases;
        if (Tenancy.Cardinality == JasperFx.Descriptors.DatabaseCardinality.Single)
        {
            databases = [Database];
        }
        else
        {
            await RefreshTenantsAsync(token).ConfigureAwait(false);
            databases = Tenancy.AllDatabases();
        }

        // The first segment in the order the all-tenants read pages them: by tenant, ordinally.
        var segments = new List<(FisherDatabase Database, string Tenant)>();
        foreach (var database in databases)
        {
            if (!await TableExistsAsync(database, mapping, token).ConfigureAwait(false))
            {
                continue;
            }

            var tenants = mapping.IsConjoined
                ? await TenantsInAsync(database, mapping, token).ConfigureAwait(false)
                : [database.TenantId ?? StorageConstants.DefaultTenantId];

            segments.AddRange(tenants.Select(tenant => (database, tenant)));
        }

        if (segments.Count == 0)
        {
            // Nothing to page through, but a table may still exist with no rows — explain the default
            // tenant's segment shape there, or report no table.
            var withTable = databases.FirstOrDefault();
            var exists = withTable is not null && await TableExistsAsync(withTable, mapping, token).ConfigureAwait(false);
            return (withTable ?? Database, StorageConstants.DefaultTenantId, exists);
        }

        var first = segments
            .OrderBy(x => x.Tenant, StringComparer.Ordinal)
            .ThenBy(x => x.Database.Identifier, StringComparer.Ordinal)
            .First();

        return (first.Database, first.Tenant, true);
    }

    /// <summary>One row of <c>EXPLAIN QUERY PLAN</c>: id, parent, and SQLite's prose.</summary>
    internal readonly record struct ExplainedStep(int Id, int ParentId, string Detail);

    internal static async Task<List<ExplainedStep>> ReadExplainedStepsAsync(DbDataReader reader,
        CancellationToken token)
    {
        var steps = new List<ExplainedStep>();

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            // id, parent, notused, detail — the shape SQLite has documented since 3.24, and what
            // FisherQueryProvider.ExplainAsync reads too.
            steps.Add(new ExplainedStep(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(3)));
        }

        return steps;
    }

    private static readonly Regex UsingIndex = new(@"\bUSING (?:COVERING )?INDEX (\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    ///     The contract's reading of SQLite's prose (jasperfx#1078).
    /// </summary>
    /// <remarks>
    ///     <see cref="DocumentQueryPlan.ScansTable" /> is a <c>SCAN</c> of the document table itself —
    ///     never of some other object the plan names, such as a <c>json_each</c> virtual table or a
    ///     subquery, which would read as a full scan of the documents when it is not one.
    /// </remarks>
    internal static DocumentQueryPlan PlanFrom(DocumentMapping mapping, IReadOnlyList<ExplainedStep> steps)
    {
        var table = mapping.TableName.Name;

        var scanOfTable = new Regex($@"^SCAN (?:TABLE )?{Regex.Escape(table)}(?:\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var indexes = steps
            .Select(x => UsingIndex.Match(x.Detail))
            .Where(x => x.Success)
            .Select(x => x.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new DocumentQueryPlan("SQLite", PlanTextOf(steps))
        {
            RowsExamined = null,
            RowsReturned = null,
            IndexesUsed = indexes,
            ScansTable = steps.Any(x => scanOfTable.IsMatch(x.Detail)),
            IsActual = false
        };
    }

    /// <summary>The plan as the <c>sqlite3</c> shell prints it — one step per line, nested by parent.</summary>
    private static string PlanTextOf(IReadOnlyList<ExplainedStep> steps)
    {
        var depth = new Dictionary<int, int>();
        var text = new StringBuilder("QUERY PLAN");

        foreach (var step in steps)
        {
            var level = step.ParentId != 0 && depth.TryGetValue(step.ParentId, out var parent) ? parent + 1 : 0;
            depth[step.Id] = level;

            text.AppendLine();
            text.Append(new string(' ', level * 3)).Append("|--").Append(step.Detail);
        }

        return text.ToString();
    }
}
