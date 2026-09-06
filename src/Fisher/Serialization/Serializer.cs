using System.Text.Json;
using Weasel.Core;

namespace Fisher.Serialization;

/// <summary>
///     Fisher's default serializer: System.Text.Json, over the shared
///     <see cref="SystemTextJsonSerializer" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>The body is gone, and that is the point (weasel#555).</b> This class and Polecat's were
///         byte-identical — the same options plumbing, the same <c>ToJson</c>/<c>FromJson</c> matrix,
///         the same <c>EnumStorage</c>/<c>Casing</c>/<c>NonPublicMembers</c> handling, and the same
///         <c>[UnconditionalSuppressMessage]</c> justifications on every reflection-based member. All
///         of it now lives in <see cref="SystemTextJsonSerializer" />, including the suppressions, so
///         the trim/AOT contract Fisher documents is the one the shared base declares rather than a
///         second copy that could drift from it.
///     </para>
///     <para>
///         What stays here is the identity: the type name Fisher's <c>StoreOptions.Serializer</c>
///         defaults to and that applications subclass, in Fisher's own namespace. The two interfaces
///         are declarations rather than work —
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <see cref="ISerializer" /> is Fisher's own extension of
///                 <c>Weasel.Core.ISerializer</c>, adding the string-based
///                 <c>FromJson&lt;T&gt;(string)</c> / <c>FromJson(Type, string)</c> overloads. The base
///                 already carries both, with the propagating
///                 <c>[RequiresUnreferencedCode]</c>/<c>[RequiresDynamicCode]</c> those two — and only
///                 those two — are meant to have.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="Weasel.Storage.IStorageSerializer" /> is the dialect-neutral seam of the
///                 shared closed-shape storage runtime. It lives in Weasel.Storage, which references
///                 Weasel.Core, so the base cannot declare it — but the base carries every member with
///                 BCL-typed signatures, which is what lets this declaration be satisfied entirely by
///                 inheritance. That is why <see cref="Weasel.Storage.StorageSerializerAdapter" />
///                 never wraps the default serializer.
///             </description>
///         </item>
///     </list>
/// </remarks>
public class Serializer : SystemTextJsonSerializer, ISerializer, Weasel.Storage.IStorageSerializer
{
    /// <summary>Construct with <see cref="SystemTextJsonSerializer.DefaultOptions" />.</summary>
    public Serializer()
    {
    }

    /// <summary>Construct over caller-supplied options.</summary>
    public Serializer(JsonSerializerOptions options) : base(options)
    {
    }
}
