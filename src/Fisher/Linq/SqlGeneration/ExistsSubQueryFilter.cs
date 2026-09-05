using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A correlated existence test over a collection member —
///     <c>exists (select 1 from json_each(data, '$.tags') as each_1 where each_1.key is not null and (…))</c>.
/// </summary>
/// <remarks>
///     <para>
///         The shape behind <c>Contains</c>, <c>Any(predicate)</c> and — negated twice — <c>All</c>:
///         <c>All(p)</c> renders as <c>not exists (… where not (p))</c>, which is also why an empty or
///         absent collection satisfies <c>All</c> (vacuous truth, matching <c>Enumerable.All</c> over an
///         empty sequence).
///     </para>
///     <para>
///         The <c>key is not null</c> guard is not decoration: <c>json_each</c> over a member stored as
///         JSON <c>null</c> yields one row holding that null, and without the guard any predicate a NULL
///         satisfies — <c>Contains(null)</c>, <c>c =&gt; c.Port == null</c>, or <c>All</c>'s negated arm
///         — would silently match a document whose collection is null. Array element rows always carry
///         their index as <c>key</c>, so the guard removes exactly that one phantom row. See
///         <see cref="Members.CollectionMember" />.
///     </para>
/// </remarks>
internal class ExistsSubQueryFilter : ISqlFragment
{
    private readonly string _source;
    private readonly string _alias;
    private readonly ISqlFragment? _where;
    private readonly bool _negated;

    /// <param name="where">The element predicate, or null for a bare <c>Any()</c>.</param>
    public ExistsSubQueryFilter(string source, string alias, ISqlFragment? where, bool negated = false)
    {
        _source = source;
        _alias = alias;
        _where = where;
        _negated = negated;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append(_negated ? "not exists (select 1 from " : "exists (select 1 from ");
        builder.Append(_source);
        builder.Append(" as ");
        builder.Append(_alias);
        builder.Append(" where ");
        builder.Append(_alias);
        builder.Append(".key is not null");

        if (_where != null)
        {
            builder.Append(" and ");
            _where.Apply(builder);
        }

        builder.Append(')');
    }
}

/// <summary>
///     A comparison against how many elements a collection member holds, optionally filtered —
///     <c>(select count(*) from json_each(data, '$.tags') as each_1 where each_1.key is not null) &gt; @p0</c>.
/// </summary>
/// <remarks>
///     <c>count(*)</c> over <c>json_each</c> rather than <c>json_array_length</c>, for two reasons that
///     both concern the degenerate cases. An absent key gives <c>json_each</c> zero rows where
///     <c>json_array_length</c> gives NULL — and "the key is not there" counting as zero elements is
///     the same honest answer <c>IsEmpty()</c> already gives. And a member stored as JSON <c>null</c>
///     gives one phantom row, which the same <c>key is not null</c> guard as
///     <see cref="ExistsSubQueryFilter" /> removes; without it a null collection would count 1.
/// </remarks>
internal class CollectionCountFilter : ISqlFragment
{
    private readonly string _source;
    private readonly string _alias;
    private readonly ISqlFragment? _where;
    private readonly string _op;
    private readonly object _value;

    public CollectionCountFilter(string source, string alias, ISqlFragment? where, string op, object value)
    {
        _source = source;
        _alias = alias;
        _where = where;
        _op = op;
        _value = value;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append("(select count(*) from ");
        builder.Append(_source);
        builder.Append(" as ");
        builder.Append(_alias);
        builder.Append(" where ");
        builder.Append(_alias);
        builder.Append(".key is not null");

        if (_where != null)
        {
            builder.Append(" and ");
            _where.Apply(builder);
        }

        builder.Append(") ");
        builder.Append(_op);
        builder.Append(' ');
        builder.AppendParameter(_value);
    }
}
