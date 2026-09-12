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

    /// <summary>
    ///     The member types an embedding may be declared as. Anything that serializes to a JSON array
    ///     of numbers works at query time; this is the list the declaration checks so a typo is caught
    ///     when the store is configured rather than when the first search returns nothing.
    /// </summary>
    internal static bool IsVectorType(Type type)
        => type == typeof(float[])
           || type == typeof(ReadOnlyMemory<float>)
           || type == typeof(ReadOnlyMemory<float>?)
           || type == typeof(Memory<float>)
           || type == typeof(double[])
           || type == typeof(List<float>)
           || type == typeof(IReadOnlyList<float>)
           || type == typeof(IList<float>)
           || type == typeof(IEnumerable<float>);
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
