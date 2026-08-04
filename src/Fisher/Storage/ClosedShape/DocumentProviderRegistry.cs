using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Weasel.Core.Identity;
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
        => (DocumentProvider<T>)_providers.GetOrAdd(typeof(T), _ => BuildProviderFor(_options.Schema.For<T>()));

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
            _ => throw new NotSupportedException(
                $"Fisher cannot store '{mapping.DocumentType.FullName}' by its '{mapping.IdType.Name}' " +
                $"identity. Supported identity types are " +
                $"{string.Join(", ", DocumentMapping.SupportedIdTypes.Select(x => x.Name))}.")
        };

        return buildTyped.MakeGenericMethod(mapping.DocumentType, mapping.IdType)
            .Invoke(this, [mapping, identification])!;
    }

    private DocumentProvider<TDoc> BuildTypedProvider<TDoc, TId>(
        DocumentMapping mapping, IIdentification<TDoc, TId> identification)
        where TDoc : notnull
        where TId : notnull
    {
        var descriptor = SqliteDocumentStorageDescriptorBuilder.Build(mapping, identification, _options);

        var queryOnly = new QueryOnlyFisherStorage<TDoc, TId>(mapping, descriptor);

        FisherDocumentStorage<TDoc, TId> lightweight = descriptor.ConcurrencyMode == ConcurrencyMode.Optimistic
            ? new OptimisticLightweightFisherStorage<TDoc, TId>(mapping, descriptor)
            : new UnversionedLightweightFisherStorage<TDoc, TId>(mapping, descriptor);

        FisherDocumentStorage<TDoc, TId> identityMap = descriptor.ConcurrencyMode == ConcurrencyMode.Optimistic
            ? new OptimisticIdentityMapFisherStorage<TDoc, TId>(mapping, descriptor)
            : new UnversionedIdentityMapFisherStorage<TDoc, TId>(mapping, descriptor);

        // Fisher has no dirty tracking, as Polecat has none: the identity-map storage takes that slot
        // because it is the closest tracking mode on offer.
        return new DocumentProvider<TDoc>(queryOnly, lightweight, identityMap, identityMap);
    }
}
