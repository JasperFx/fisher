using System.Reflection;
using Weasel.Core;

namespace Fisher.Storage;

/// <summary>
///     A real foreign key from one document table to another's <c>id</c> (fisher#38).
/// </summary>
/// <remarks>
///     <para>
///         <b>SQLite's reputation invites the question, so: it supports this completely.</b> Foreign
///         keys, <c>ON DELETE CASCADE</c> and <c>ON DELETE SET NULL</c> are all there. Enforcement is
///         per-connection through <c>PRAGMA foreign_keys</c> and off by default <em>in the SQLite
///         library</em> — but not here, because Weasel's default profile turns it on for every
///         connection Fisher opens. That is exactly why fisher#6 happened, and it means a document
///         foreign key is enforced the moment it is declared.
///     </para>
///     <para>
///         <b>The child column is a <c>VIRTUAL</c> generated column, and that was the thing to verify
///         before building anything.</b> fisher#38 flagged it as a possible blocker: SQLite might have
///         refused a generated column as a foreign key child, which would have forced a <c>STORED</c>
///         or written column and reopened the write-path question fisher#2 closed. **It does not.**
///         Verified against SQLite 3.50.4 (the version Microsoft.Data.Sqlite 10.0.9 bundles): the
///         table is created, an orphan insert fails with <c>FOREIGN KEY constraint failed</c>, a row
///         whose key is absent from the JSON is allowed (the column is NULL, and SQLite exempts NULL
///         child values), <c>ON DELETE CASCADE</c> works, and <c>pragma_foreign_key_list</c> reports
///         it. So the write path stays untouched and fisher#2's decision holds.
///     </para>
///     <para>
///         <b>Declaring a foreign key duplicates the member implicitly</b>, and that is a genuine
///         divergence from both siblings. A foreign key needs a real column, and on a Fisher document
///         table a member lives in <c>data</c> — so the alternative is an error message telling the
///         caller to write a <c>Duplicate(...)</c> line that has no other purpose. On Marten and
///         Polecat the two are already separate concepts because their duplicated columns are
///         <em>written</em>; here the duplicated column costs nothing but index space, so folding one
///         into the other loses nothing. An explicit <c>Duplicate</c> on the same member still wins,
///         because <c>DocumentMapping.Duplicate</c> is idempotent.
///     </para>
///     <para>
///         <b>The referenced side is always the other type's <c>id</c>.</b> SQLite requires a foreign
///         key to reference a <c>PRIMARY KEY</c> or <c>UNIQUE</c> column, and a document table's
///         identity is its primary key. Referencing a duplicated field would need that field's index
///         to be <c>UNIQUE</c>, which is a shape nobody has asked for.
///     </para>
/// </remarks>
internal sealed class DocumentForeignKey
{
    internal DocumentForeignKey(MemberInfo[] members, Type referencedType, CascadeAction onDelete)
    {
        Members = members;
        ReferencedType = referencedType;
        OnDelete = onDelete;
    }

    /// <summary>The member chain on the referencing document that holds the other's identity.</summary>
    internal MemberInfo[] Members { get; }

    /// <summary>The document type whose table is referenced.</summary>
    internal Type ReferencedType { get; }

    /// <summary>What happens to the referencing rows when a referenced row is deleted.</summary>
    internal CascadeAction OnDelete { get; }

    internal IEnumerable<string> MemberNames => Members.Select(x => x.Name);

    /// <summary>
    ///     Refuse a key SQLite would accept in DDL and fail on at the first write (fisher#488).
    /// </summary>
    /// <remarks>
    ///     A conjoined table's primary key is <c>(tenant_id, id)</c>, so <c>id</c> alone is not a key it
    ///     can reference, and SQLite says so only when a row is written: <c>foreign key mismatch</c>, a
    ///     long way from the configuration line. Between two conjoined types the key is composite on the
    ///     tenant. A single-tenant child has no tenant to put in it, and a cascade that clears the key
    ///     would clear <c>tenant_id</c> too.
    /// </remarks>
    internal void AssertTenancyAgrees(DocumentMapping mapping, DocumentMapping referenced)
    {
        if (!referenced.IsConjoined)
        {
            return;
        }

        var key = $"'{mapping.DocumentType.Name}.{string.Join(".", MemberNames)}'";

        if (!mapping.IsConjoined)
        {
            throw new InvalidOperationException(
                $"The foreign key on {key} references '{referenced.DocumentType.Name}', which is MultiTenanted(), "
                + $"so its rows are keyed by tenant as well as id and '{mapping.DocumentType.Name}' has no tenant "
                + $"to reference them with. Mark '{mapping.DocumentType.Name}' MultiTenanted() too.");
        }

        if (OnDelete is CascadeAction.SetNull or CascadeAction.SetDefault)
        {
            throw new InvalidOperationException(
                $"The foreign key on {key} is between two MultiTenanted() types, so it includes tenant_id, and "
                + $"OnDelete = {OnDelete} would clear that as well. Use Cascade, Restrict or NoAction.");
        }
    }
}
