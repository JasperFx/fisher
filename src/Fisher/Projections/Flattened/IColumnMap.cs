using Weasel.Sqlite;

namespace Fisher.Projections.Flattened;

/// <summary>
///     How one column participates in a flat table's upsert.
/// </summary>
/// <remarks>
///     <para>
///         Both halves of SQLite's <c>insert … on conflict … do update</c> are described here: the
///         value expression for the insert branch, and the assignment for the update branch. They are
///         deliberately not derived from each other — an <c>Increment</c> inserts a starting value but
///         updates by adding to what is already there, and that asymmetry is the entire point.
///     </para>
///     <para>
///         <strong>An unqualified column on the right of the update assignment is the pre-update
///         row.</strong> That is what makes <c>"a" = "a" + @p1</c> an increment rather than a
///         self-assignment; <c>excluded."a"</c> would be the value the insert branch would have
///         written. Polecat spells the same thing <c>target.[a]</c> inside its <c>MERGE</c>.
///     </para>
/// </remarks>
internal interface IColumnMap
{
    string ColumnName { get; }

    /// <summary>
    ///     True when the column's value comes from the event and therefore needs a parameter.
    ///     False for <c>Increment(name)</c>, <c>Decrement(name)</c> and <c>SetValue</c>, which are
    ///     entirely determined at configuration time.
    /// </summary>
    bool RequiresInput { get; }

    /// <summary>The assignment for the <c>do update set</c> clause.</summary>
    string UpdateExpression(string parameterName);

    /// <summary>The value expression for the insert branch.</summary>
    string InsertExpression(string parameterName);
}

/// <summary>Assigns an event member straight onto the column.</summary>
internal sealed class MemberMap : IColumnMap
{
    public MemberMap(string columnName) => ColumnName = columnName;

    public string ColumnName { get; }
    public bool RequiresInput => true;

    public string UpdateExpression(string parameterName)
        => $"{SchemaUtils.QuoteName(ColumnName)} = {parameterName}";

    public string InsertExpression(string parameterName) => parameterName;
}

/// <summary>Adds an event member's value to the column.</summary>
internal sealed class IncrementMemberMap : IColumnMap
{
    public IncrementMemberMap(string columnName) => ColumnName = columnName;

    public string ColumnName { get; }
    public bool RequiresInput => true;

    public string UpdateExpression(string parameterName)
        => $"{SchemaUtils.QuoteName(ColumnName)} = {SchemaUtils.QuoteName(ColumnName)} + {parameterName}";

    // A row that does not exist yet starts at the increment itself, not at zero-plus-it.
    public string InsertExpression(string parameterName) => parameterName;
}

/// <summary>Subtracts an event member's value from the column.</summary>
internal sealed class DecrementMemberMap : IColumnMap
{
    public DecrementMemberMap(string columnName) => ColumnName = columnName;

    public string ColumnName { get; }
    public bool RequiresInput => true;

    public string UpdateExpression(string parameterName)
        => $"{SchemaUtils.QuoteName(ColumnName)} = {SchemaUtils.QuoteName(ColumnName)} - {parameterName}";

    /// <remarks>
    ///     <b>Negated</b> (fisher#183, over the jasperfx#773 ruling). The insert branch applies the
    ///     event to an implicit zero row, so a first event carrying <c>5</c> must leave the column at
    ///     <c>-5</c>: a decrement must never leave a column higher than it found it, and inserting
    ///     the parameter unchanged made a decrement raise it. Marten was the only store that had this
    ///     right; Fisher, Polecat and the lifted Weasel DSL were the majority and the majority was
    ///     wrong. <c>-@p1</c> inside a <c>values (…)</c> list is valid SQLite, which is why the
    ///     negation lives here rather than behind a dialect hook (weasel#574).
    /// </remarks>
    public string InsertExpression(string parameterName) => "-" + parameterName;
}

/// <summary>Adds one to the column, with nothing read from the event.</summary>
internal sealed class IncrementMap : IColumnMap
{
    public IncrementMap(string columnName) => ColumnName = columnName;

    public string ColumnName { get; }
    public bool RequiresInput => false;

    public string UpdateExpression(string parameterName)
        => $"{SchemaUtils.QuoteName(ColumnName)} = {SchemaUtils.QuoteName(ColumnName)} + 1";

    public string InsertExpression(string parameterName) => "1";
}

/// <summary>Subtracts one from the column, with nothing read from the event.</summary>
internal sealed class DecrementMap : IColumnMap
{
    public DecrementMap(string columnName) => ColumnName = columnName;

    public string ColumnName { get; }
    public bool RequiresInput => false;

    public string UpdateExpression(string parameterName)
        => $"{SchemaUtils.QuoteName(ColumnName)} = {SchemaUtils.QuoteName(ColumnName)} - 1";

    // A first sighting inserts zero rather than -1, which is deliberate and is NOT symmetric with
    // IncrementMap any more: that one moved to inserting 1 (marten#5341), and jasperfx#773 left the
    // by-column decrement's insert value open while settling the member-valued form above. All four
    // stores insert 0 here, so this stays until the ruling arrives.
    public string InsertExpression(string parameterName) => "0";
}

/// <summary>Sets the column to a configured string.</summary>
/// <remarks>
///     The value lands in a SQL string literal rather than a parameter, because it is fixed at
///     configuration time and baking it in keeps the parameter list aligned with the maps that
///     genuinely read from the event. An embedded quote therefore has to be doubled — otherwise it
///     terminates the literal and the remainder is parsed as SQL. Same hazard, and the same fix, as
///     polecat#390.
/// </remarks>
internal sealed class SetStringValueMap : IColumnMap
{
    private readonly string _literal;

    public SetStringValueMap(string columnName, string value)
    {
        ColumnName = columnName;
        _literal = $"'{value.Replace("'", "''")}'";
    }

    public string ColumnName { get; }
    public bool RequiresInput => false;

    public string UpdateExpression(string parameterName)
        => $"{SchemaUtils.QuoteName(ColumnName)} = {_literal}";

    public string InsertExpression(string parameterName) => _literal;
}

/// <summary>Sets the column to a configured integer.</summary>
internal sealed class SetIntValueMap : IColumnMap
{
    private readonly int _value;

    public SetIntValueMap(string columnName, int value)
    {
        ColumnName = columnName;
        _value = value;
    }

    public string ColumnName { get; }
    public bool RequiresInput => false;

    public string UpdateExpression(string parameterName)
        => $"{SchemaUtils.QuoteName(ColumnName)} = {_value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public string InsertExpression(string parameterName)
        => _value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
