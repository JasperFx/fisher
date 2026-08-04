using Weasel.Sqlite.Tables;

namespace Fisher.Storage.Sequences;

/// <summary>
///     Weasel table definition for <c>fi_hilo</c> — one row per logical sequence, holding the highest
///     "hi" allocation handed out so far.
/// </summary>
/// <remarks>
///     <para>
///         <c>hi_value</c> is INTEGER, which in SQLite is a 64-bit signed integer, so it matches the
///         <c>long</c> the shared <c>HiloSequenceBase</c> arithmetic works in without a widening step.
///     </para>
///     <para>
///         The entity name is the primary key, which is what makes the upsert in
///         <see cref="HiloSequence" /> atomic: <c>ON CONFLICT</c> needs a uniqueness constraint to
///         target, and this is it.
///     </para>
/// </remarks>
internal class HiloTable : Table
{
    /// <summary>The family suffix — <c>fi_hilo</c>, or <c>&lt;schema&gt;_fi_hilo</c>.</summary>
    public const string TableSuffix = "hilo";

    public HiloTable(string schemaName)
        : base(FisherTableNaming.ObjectFor(schemaName, TableSuffix))
    {
        AddColumn("entity_name", "TEXT").NotNull().AsPrimaryKey();
        AddColumn("hi_value", "INTEGER").NotNull().DefaultValue(0);
    }
}
