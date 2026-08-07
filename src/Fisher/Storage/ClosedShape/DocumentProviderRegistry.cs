using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core.Reflection;
using Weasel.Core.Identity;
using Weasel.Core.Sequences;
using Weasel.Storage;

namespace Fisher.Storage.ClosedShape;

/// <summary>
///     The store's <see cref="IProviderGraph" />: one cached <see cref="DocumentProvider{T}" /> per
///     document type, each holding the four storage flavors.
/// </summary>
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification =
        "Closes the shared identity strategies and storage generics over runtime document/id types via MakeGenericMethod, once per document type at registration. AOT consumers register document types explicitly per the AOT publishing guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2060:MakeGenericMethod",
    Justification = "Registration-time generic closing over registered document types.")]
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Registration-time generic closing over registered document types.")]
internal class DocumentProviderRegistry : IProviderGraph
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> _providers = new();
    private readonly StoreOptions _options;

    public DocumentProviderRegistry(StoreOptions options)
    {
        _options = options;
    }

    public DocumentProvider<T> StorageFor<T>() where T : notnull
        => (DocumentProvider<T>)_providers.GetOrAdd(typeof(T), _ =>
        {
            var mapping = _options.Schema.MappingFor(typeof(T));

            // A registered sub-class resolves to its hierarchy's mapping, which is how the whole
            // hierarchy shares one table. Its storage wraps the base's rather than being built from the
            // mapping directly: the descriptor's selectors materialise each row as whatever its
            // discriminator says, which only type-checks against the base.
            return mapping.DocumentType != typeof(T)
                ? BuildSubClassProviderFor<T>(mapping)
                : BuildProviderFor(mapping);
        });

    /// <summary>
    ///     Wrap the hierarchy base's provider so every flavor narrows to one sub-class.
    /// </summary>
    /// <remarks>
    ///     Reflection because the base type and the identity type are both runtime values here — the
    ///     caller named only the sub-class. Once per sub-class per store; the result is cached by the
    ///     dictionary this is called from.
    /// </remarks>
    private object BuildSubClassProviderFor<T>(DocumentMapping mapping) where T : notnull
        => typeof(DocumentProviderRegistry)
            .GetMethod(nameof(BuildTypedSubClassProvider), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(typeof(T), mapping.DocumentType, mapping.StoredIdType)
            .Invoke(this, [mapping])!;

    private DocumentProvider<TDoc> BuildTypedSubClassProvider<TDoc, TBase, TId>(DocumentMapping mapping)
        where TDoc : notnull, TBase
        where TBase : notnull
        where TId : notnull
    {
        var parent = StorageFor<TBase>();

        SubClassFisherStorage<TDoc, TBase, TId> Wrap(IDocumentStorage<TBase> storage)
            => new((IDocumentStorage<TBase, TId>)storage, mapping);

        return new DocumentProvider<TDoc>(
            Wrap(parent.QueryOnly), Wrap(parent.Lightweight), Wrap(parent.IdentityMap),
            Wrap(parent.DirtyTracking));
    }

    public void Append<T>(DocumentProvider<T> provider) where T : notnull => _providers[typeof(T)] = provider;

    /// <summary>
    ///     Close the storage generics over the document type and its identity type.
    /// </summary>
    /// <remarks>
    ///     The four identity types <see cref="DocumentMapping.SupportedIdTypes" /> names, and nothing
    ///     else — a strongly typed id wrapper needs a strategy that unwraps it, and Fisher has no
    ///     strong-typed-id support anywhere. The numeric pair resolve their sequence through
    ///     <c>ISequenceSource</c>, which is <c>FisherDatabase</c>, keyed on the document type.
    /// </remarks>
    private object BuildProviderFor(DocumentMapping mapping)
    {
        var buildTyped = typeof(DocumentProviderRegistry)
            .GetMethod(nameof(BuildTypedProvider), BindingFlags.NonPublic | BindingFlags.Instance)!;

        object identification = mapping.IdType switch
        {
            // Wrapped so the id crosses the ADO.NET boundary as Fisher's canonical lowercase text —
            // see SqliteGuidIdentification for what goes wrong without it.
            var t when t == typeof(Guid) => Activator.CreateInstance(
                typeof(SqliteGuidIdentification<>).MakeGenericType(mapping.DocumentType),
                Activator.CreateInstance(
                    typeof(SequentialGuidIdentification<>).MakeGenericType(mapping.DocumentType),
                    mapping.IdMember))!,
            var t when t == typeof(string) => Activator.CreateInstance(
                typeof(StringIdentification<>).MakeGenericType(mapping.DocumentType), mapping.IdMember)!,
            // The document type is the sequence key, which SequenceFactory then resolves to a name —
            // so two types sharing a configured SequenceName share one allocation.
            var t when t == typeof(int) => Activator.CreateInstance(
                typeof(HiloIntIdentification<>).MakeGenericType(mapping.DocumentType),
                mapping.IdMember, mapping.DocumentType)!,
            var t when t == typeof(long) => Activator.CreateInstance(
                typeof(HiloLongIdentification<>).MakeGenericType(mapping.DocumentType),
                mapping.IdMember, mapping.DocumentType)!,
            // A strong-typed id wrapper: the document keeps the wrapper, the column keeps the inner
            // value, and StrongTypedIdentification is the only thing that knows the difference.
            var t when StrongTypedId.TryResolve(t, out var info) => BuildStrongTyped(mapping, info),
            _ => throw new NotSupportedException(
                $"Fisher cannot store '{mapping.DocumentType.FullName}' by its '{mapping.IdType.Name}' " +
                $"identity. Supported identity types are " +
                $"{string.Join(", ", DocumentMapping.SupportedIdTypes.Select(x => x.Name))}, or a " +
                "wrapper around one with a single gettable property and a matching constructor or " +
                "static builder.")
        };

        return buildTyped.MakeGenericMethod(mapping.DocumentType, mapping.IdType)
            .Invoke(this, [mapping, identification])!;
    }

    /// <summary>
    ///     Close <see cref="StrongTypedIdentification{TDoc,TId,TInner}" /> over the document, the
    ///     wrapper and the type it wraps, with a generator matching the inner type.
    /// </summary>
    /// <remarks>
    ///     The generators mirror the raw strategies exactly — version-7 Guid, or the document type's
    ///     Hi-Lo sequence — so a wrapper gets the same ids its unwrapped counterpart would. A
    ///     string-backed wrapper gets none, because <c>StringIdentification</c> generates none either:
    ///     a string key is externally assigned.
    /// </remarks>
    private static object BuildStrongTyped(DocumentMapping mapping, ValueTypeInfo info)
    {
        var inner = info.SimpleType;
        var documentType = mapping.DocumentType;

        object? generate = inner switch
        {
            var t when t == typeof(Guid) => (Func<ISequenceSource, Guid>)(_ => Guid.CreateVersion7()),
            var t when t == typeof(int) => (Func<ISequenceSource, int>)(s =>
                s.SequenceFor(documentType).NextInt()),
            var t when t == typeof(long) => (Func<ISequenceSource, long>)(s =>
                s.SequenceFor(documentType).NextLong()),
            _ => null
        };

        return Activator.CreateInstance(
            typeof(StrongTypedIdentification<,,>).MakeGenericType(documentType, mapping.IdType, inner),
            info, mapping.IdMember, generate)!;
    }

    private DocumentProvider<TDoc> BuildTypedProvider<TDoc, TId>(
        DocumentMapping mapping, IIdentification<TDoc, TId> identification)
        where TDoc : notnull
        where TId : notnull
    {
        var descriptor = SqliteDocumentStorageDescriptorBuilder.Build(mapping, identification, _options);

        var queryOnly = new QueryOnlyFisherStorage<TDoc, TId>(mapping, descriptor);

        FisherDocumentStorage<TDoc, TId> lightweight = descriptor.ConcurrencyMode switch
        {
            ConcurrencyMode.Optimistic => new OptimisticLightweightFisherStorage<TDoc, TId>(mapping, descriptor),
            ConcurrencyMode.Numeric => new NumericLightweightFisherStorage<TDoc, TId>(mapping, descriptor),
            _ => new UnversionedLightweightFisherStorage<TDoc, TId>(mapping, descriptor)
        };

        FisherDocumentStorage<TDoc, TId> identityMap = descriptor.ConcurrencyMode switch
        {
            ConcurrencyMode.Optimistic => new OptimisticIdentityMapFisherStorage<TDoc, TId>(mapping, descriptor),
            ConcurrencyMode.Numeric => new NumericIdentityMapFisherStorage<TDoc, TId>(mapping, descriptor),
            _ => new UnversionedIdentityMapFisherStorage<TDoc, TId>(mapping, descriptor)
        };

        // Fisher has no dirty tracking, as Polecat has none: the identity-map storage takes that slot
        // because it is the closest tracking mode on offer.
        return new DocumentProvider<TDoc>(queryOnly, lightweight, identityMap, identityMap);
    }
}
