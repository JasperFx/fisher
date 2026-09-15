using System.Reflection;
using Fisher.Linq.Members;
using JasperFx.Events.Vectors;

namespace Fisher.Storage.Vectors;

/// <summary>
///     A declared vector member on a document type (fisher#241): which member holds the embedding,
///     how long it is, and which distance a search over it uses when the caller names none.
/// </summary>
/// <remarks>
///     <para>
///         <b>Metadata, not a schema object.</b> There is no side table and no trigger, deliberately.
///         SQLite has no built-in that turns a JSON array into a float32 BLOB, so a trigger keeping a
///         BLOB column in step would have to call an application-defined function — and then every
///         foreign writer on the file (an EF Core context, the <c>sqlite3</c> shell, a restore) would
///         fail with "no such function". A side table maintained on Fisher's own write path would go
///         silently stale under those same writers, which is exactly the failure the full-text design
///         refused. So a search reads the embedding straight out of <c>data</c> through
///         <c>json_extract</c> and the registered <c>fi_vector_distance</c> function, which cannot drift.
///         Brute force, <c>ORDER BY … LIMIT k</c> — the right answer at Fisher's scale, and the seam a
///         native index would slot behind if a workload ever outgrew it.
///     </para>
///     <para>
///         What the declaration buys: a search on a type with no declared index is refused by name
///         (the full-text rule — the alternative is a valid query that scans nothing), the query
///         vector's length is checked against <see cref="Dimensions" /> before any SQL runs, and the
///         default <see cref="Distance" /> is pinned once rather than repeated at every call.
///     </para>
/// </remarks>
internal sealed class VectorIndex
{
    internal VectorIndex(MemberInfo[] memberChain, int dimensions, DistanceFunction distance)
    {
        MemberChain = memberChain;
        Dimensions = dimensions;
        Distance = distance;
    }

    internal MemberInfo[] MemberChain { get; }

    internal int Dimensions { get; }

    internal DistanceFunction Distance { get; }

    internal string MemberName => string.Join(".", MemberChain.Select(x => x.Name));

    /// <summary>The <c>json_extract</c> locator for the member, casing and all.</summary>
    internal string Locator(MemberFactory members) => members.ResolveMember(MemberChain).RawLocator;

    internal bool Covers(MemberInfo[] chain)
        => chain.Length == MemberChain.Length
           && chain.Zip(MemberChain).All(pair => pair.First.MetadataToken == pair.Second.MetadataToken
                                                  && pair.First.Module == pair.Second.Module);

    internal static readonly Type[] AcceptedTypes =
    [
        typeof(float[]),
        typeof(ReadOnlyMemory<float>),
        typeof(ReadOnlyMemory<float>?),
        typeof(Memory<float>),
        typeof(double[]),
        typeof(List<float>),
        typeof(IReadOnlyList<float>),
        typeof(IList<float>),
        typeof(IEnumerable<float>)
    ];

    /// <summary>
    ///     <see cref="AcceptedTypes" /> spelled the way a caller would declare the member, for the
    ///     refusal message.
    /// </summary>
    /// <remarks>
    ///     ⚠️ <b>Derived rather than written out beside the array, because a source of truth is only one
    ///     if the other form is computed from it.</b> The first cut of fisher#288 kept this as a
    ///     hand-maintained <c>const</c> next to <see cref="AcceptedTypes" /> — which is the very drift
    ///     the array was introduced to remove, just moved one line down: adding a tenth type and
    ///     forgetting the string left the message confidently listing nine, with nothing failing.
    /// </remarks>
    internal static readonly string AcceptedTypesDescription = Describe(AcceptedTypes);

    /// <summary>
    ///     A type as C# would spell it — <c>float[]</c>, <c>ReadOnlyMemory&lt;float&gt;?</c> — which is
    ///     what a caller needs to see to fix a declaration.
    /// </summary>
    /// <remarks>
    ///     JasperFx's <c>ShortNameInCode()</c> is the obvious reach and produces <c>Single[]</c> and
    ///     <c>Nullable&lt;ReadOnlyMemory&lt;Single&gt;&gt;</c> — accurate, and not spellings anybody
    ///     writes. Measured before this was written rather than assumed.
    /// </remarks>
    private static string NameFor(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return NameFor(inner) + "?";
        }

        if (type.IsArray)
        {
            return NameFor(type.GetElementType()!) + "[]";
        }

        if (type.IsGenericType)
        {
            var name = type.Name[..type.Name.IndexOf('`')];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(NameFor))}>";
        }

        return type == typeof(float) ? "float"
            : type == typeof(double) ? "double"
            : type.Name;
    }

    private static string Describe(IReadOnlyList<Type> types)
    {
        var names = types.Select(NameFor).ToArray();

        return names.Length == 1
            ? names[0]
            : string.Join(", ", names[..^1]) + ", or " + names[^1];
    }

    /// <summary>
    ///     The member types an embedding may be declared as. Anything that serializes to a JSON array
    ///     of numbers works at query time; this is the list the declaration checks so a typo is caught
    ///     when the store is configured rather than when the first search returns nothing.
    /// </summary>
    internal static bool IsVectorType(Type type)
        => AcceptedTypes.Contains(type);
}

/// <summary>
///     Declare a member as a vector embedding — the attribute form of
///     <c>Schema.For&lt;T&gt;().VectorIndex(x =&gt; x.Embedding, dimensions)</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class VectorIndexAttribute : Attribute
{
    public VectorIndexAttribute(int dimensions)
    {
        Dimensions = dimensions;
    }

    /// <summary>The length of every vector stored in the member.</summary>
    public int Dimensions { get; }

    /// <summary>The distance a search uses when the caller names none. Cosine by default.</summary>
    public DistanceFunction Distance { get; set; } = DistanceFunction.Cosine;
}
