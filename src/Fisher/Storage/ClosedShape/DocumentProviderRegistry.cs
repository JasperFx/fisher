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
                : BuildProviderFor<T>(mapping);
        });

    /// <summary>
    ///     Wrap the hierarchy base's provider so every flavor narrows to one sub-class.
    /// </summary>
    /// <remarks>
    ///     Reflection because the base type and the identity type are both runtime values here — the
    ///     caller named only the sub-class. Once per sub-class per store; the result is cached by the
    ///     dictionary this is called from.
    /// </remarks>
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification =
            "Document hierarchies only: the base type is a runtime value here, so this path is not Native AOT safe (fisher#384, fisher#386). A type with no sub-classes never reaches it.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060:MakeGenericMethod",
        Justification = "Document hierarchies only; see the IL3050 justification.")]
    private object BuildSubClassProviderFor<T>(DocumentMapping mapping) where T : notnull
    {
        // Registered through AddSubClass<TSub>(), which knew the base type statically (fisher#386).
        if (mapping.SubClasses.FirstOrDefault(x => x.DocumentType == typeof(T))?.ProviderFactory is { } factory)
        {
            return factory(this, mapping);
        }

        AssertReflectionIsAvailable(typeof(T),
            $"it was registered as a sub-class of '{mapping.DocumentType.Name}' by Type (AddSubClass(Type) " +
            $"or AddSubClassHierarchy()). Register it with Schema.For<{mapping.DocumentType.Name}>()" +
            $".AddSubClass<{typeof(T).Name}>() instead");

        return typeof(DocumentProviderRegistry)
            .GetMethod(nameof(BuildTypedSubClassProvider), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(typeof(T), mapping.DocumentType, mapping.StoredIdType)
            .Invoke(this, [mapping])!;
    }

    /// <summary>
    ///     A sub-class's provider with every type closed statically, for <c>AddSubClass&lt;TSub&gt;()</c>.
    /// </summary>
    /// <remarks>
    ///     The stored identity type is always one of the four canonical ones (a wrapper stores its inner
    ///     value), so this switch is complete.
    /// </remarks>
    internal object BuildSubClassProvider<TSub, TBase>(DocumentMapping mapping)
        where TSub : notnull, TBase
        where TBase : notnull
    {
        var idType = mapping.StoredIdType;

        if (idType == typeof(Guid)) return BuildTypedSubClassProvider<TSub, TBase, Guid>(mapping);
        if (idType == typeof(string)) return BuildTypedSubClassProvider<TSub, TBase, string>(mapping);
        if (idType == typeof(int)) return BuildTypedSubClassProvider<TSub, TBase, int>(mapping);
        if (idType == typeof(long)) return BuildTypedSubClassProvider<TSub, TBase, long>(mapping);

        throw new NotSupportedException(
            $"Fisher cannot store '{typeof(TSub).FullName}' by a stored identity of type '{idType.Name}'.");
    }

    /// <summary>
    ///     Refuse a reflective path by name in a Native AOT image, where it would otherwise fail with
    ///     "missing native code" and nothing about the configuration that would avoid it.
    /// </summary>
    private static void AssertReflectionIsAvailable(Type documentType, string reason)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                $"Fisher cannot build storage for '{documentType.FullName}' in a Native AOT image, because " +
                $"{reason}. See fisher#386.");
        }
    }

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
    ///     The four identity types <see cref="DocumentMapping.SupportedIdTypes" /> names, plus a
    ///     strong-typed wrapper around one of them, which gets a strategy that unwraps it. The numeric
    ///     pair resolve their sequence through <c>ISequenceSource</c>, which is <c>FisherDatabase</c>,
    ///     keyed on the document type.
    /// </remarks>
    /// <remarks>
    ///     <para>
    ///         <b>The four canonical identity types are closed statically, and that is what makes a
    ///         document write work under Native AOT</b> (fisher#384). <typeparamref name="T" /> is
    ///         already the document type here, so <c>BuildTypedProvider&lt;T, Guid&gt;</c> and its three
    ///         siblings are ordinary generic calls ILC can see and compile. Closing them with
    ///         <c>MakeGenericMethod</c>, as this used to, works under CoreCLR and throws
    ///         <c>NotSupportedException: missing native code</c> in a native image, on the first write.
    ///     </para>
    ///     <para>
    ///         A strong-typed id wrapper is still closed reflectively, because its type is a runtime
    ///         value nothing here can name. See <see cref="BuildReflectively" />.
    ///     </para>
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification =
            "Weasel's identity strategies compile accessors over the identity member, which must survive trimming. Under Native AOT the application's source-generated JsonSerializerContext references every serialized property of the document, which keeps it; measured in a native image for all four identity types (fisher#384). A member it does not keep fails as 'no identity member', which names the AOT cause.")]
    private object BuildProviderFor<T>(DocumentMapping mapping) where T : notnull
    {
        var idType = mapping.IdType;

        if (mapping.IdStrategy is null)
        {
            if (idType == typeof(Guid))
            {
                return BuildTypedProvider(mapping,
                    new SqliteGuidIdentification<T>(new SequentialGuidIdentification<T>(mapping.IdMember)));
            }

            if (idType == typeof(string))
            {
                return BuildTypedProvider(mapping, new StringIdentification<T>(mapping.IdMember));
            }

            // The document type is the sequence key, which SequenceFactory then resolves to a name —
            // so two types sharing a configured SequenceName share one allocation.
            if (idType == typeof(int))
            {
                return BuildTypedProvider(mapping, new HiloIntIdentification<T>(mapping.IdMember, typeof(T)));
            }

            if (idType == typeof(long))
            {
                return BuildTypedProvider(mapping, new HiloLongIdentification<T>(mapping.IdMember, typeof(T)));
            }
        }
        else
        {
            // A caller-supplied strategy (fisher#218). A Guid-keyed one is wrapped, never taken raw:
            // the lowercase-canonical conversion lives in the identity strategy rather than in the
            // dialect, so replacing the strategy is exactly where it could be lost -- and losing it
            // writes rows that can never be read back, silently and only for Guid-identified types.
            // See SqliteGuidIdentification.
            switch (mapping.IdStrategy)
            {
                case IIdentification<T, Guid> guid:
                    return BuildTypedProvider(mapping, new SqliteGuidIdentification<T>(guid));
                case IIdentification<T, string> text:
                    return BuildTypedProvider(mapping, text);
                case IIdentification<T, int> number:
                    return BuildTypedProvider(mapping, number);
                case IIdentification<T, long> number:
                    return BuildTypedProvider(mapping, number);
            }
        }

        // A strong-typed id named through Identity<TValue>(...) or IdStrategy<TId>(...) (fisher#386).
        if (mapping.ProviderFactory?.Invoke(this, mapping) is { } provider)
        {
            return provider;
        }

        AssertReflectionIsAvailable(typeof(T),
            $"its '{idType.Name}' identity is a strong-typed wrapper Fisher found by convention. Name it " +
            $"with Schema.For<{typeof(T).Name}>().Identity(x => x.{mapping.IdMember.Name}) so its type is " +
            "known without reflection");

        return BuildReflectively(mapping);
    }

    /// <summary>
    ///     A strong-typed id document's provider with the document, the wrapper and its inner type all
    ///     closed statically, for <c>Identity&lt;TValue&gt;(...)</c> and <c>IdStrategy&lt;TId&gt;(...)</c>.
    /// </summary>
    /// <remarks>
    ///     The same strategies <see cref="BuildStrongTyped" /> builds reflectively, with the inner type
    ///     switched over instead of reflected on: version-7 Guid, the document type's Hi-Lo sequence, or
    ///     no generator for a string. A caller-supplied strategy wins, as it does on the other paths.
    ///     Null when the mapping's identity is no longer <typeparamref name="TId" />, so the caller falls
    ///     through rather than building storage for a member that is not the identity any more.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification =
            "StrongTypedIdentification compiles accessors over the identity member and the wrapper's value property; the application's source-generated JsonSerializerContext keeps both. Measured in a native image (fisher#386).")]
    internal object? BuildStrongTypedProvider<TDoc, TId>(DocumentMapping mapping)
        where TDoc : notnull
        where TId : notnull
    {
        if (mapping.IdType != typeof(TId) || !StrongTypedId.TryResolve(typeof(TId), out var info))
        {
            return null;
        }

        if (mapping.IdStrategy is IIdentification<TDoc, TId> custom)
        {
            return BuildTypedProvider(mapping, custom);
        }

        var inner = info.SimpleType;
        var member = mapping.IdMember;

        if (inner == typeof(Guid))
        {
            return BuildTypedProvider(mapping, new StrongTypedIdentification<TDoc, TId, Guid>(info, member,
                _ => Guid.CreateVersion7()));
        }

        if (inner == typeof(string))
        {
            return BuildTypedProvider(mapping, new StrongTypedIdentification<TDoc, TId, string>(info, member, null));
        }

        if (inner == typeof(int))
        {
            return BuildTypedProvider(mapping, new StrongTypedIdentification<TDoc, TId, int>(info, member,
                s => s.SequenceFor(typeof(TDoc)).NextInt()));
        }

        if (inner == typeof(long))
        {
            return BuildTypedProvider(mapping, new StrongTypedIdentification<TDoc, TId, long>(info, member,
                s => s.SequenceFor(typeof(TDoc)).NextLong()));
        }

        return null;
    }

    /// <summary>
    ///     The reflective path: a strong-typed id wrapper, whose type only exists at runtime here.
    /// </summary>
    /// <remarks>
    ///     <b>Not Native AOT safe, and the suppressions say so rather than claiming otherwise.</b>
    ///     <c>MakeGenericMethod</c> over a value-type wrapper needs native code ILC never generated.
    ///     The canonical identity types never reach this method; see
    ///     <see cref="BuildProviderFor{T}" />.
    /// </remarks>
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = StrongTypedIdsAreReflective)]
    [UnconditionalSuppressMessage("Trimming", "IL2060:MakeGenericMethod",
        Justification = StrongTypedIdsAreReflective)]
    private object BuildReflectively(DocumentMapping mapping)
    {
        var buildTyped = typeof(DocumentProviderRegistry)
            .GetMethod(nameof(BuildTypedProvider), BindingFlags.NonPublic | BindingFlags.Instance)!;

        // A caller-supplied strategy for a wrapper is taken as it is: only a Guid-keyed one needs
        // wrapping in SqliteGuidIdentification, and that case was closed statically.
        var identification = mapping.IdStrategy
                             ?? (StrongTypedId.TryResolve(mapping.IdType, out var info)
                                 ? BuildStrongTyped(mapping, info)
                                 : throw new NotSupportedException(
                                     $"Fisher cannot store '{mapping.DocumentType.FullName}' by its " +
                                     $"'{mapping.IdType.Name}' identity. Supported identity types are " +
                                     $"{string.Join(", ", DocumentMapping.SupportedIdTypes.Select(x => x.Name))}, " +
                                     "or a wrapper around one with a single gettable property and a matching " +
                                     "constructor or static builder."));

        return buildTyped.MakeGenericMethod(mapping.DocumentType, mapping.IdType)
            .Invoke(this, [mapping, identification])!;
    }

    private const string StrongTypedIdsAreReflective =
        "Strong-typed id wrappers only. The four canonical identity types are closed statically in " +
        "BuildProviderFor<T>; a wrapper's type is a runtime value, so this path is not Native AOT safe (fisher#384, fisher#386).";

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
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = StrongTypedIdsAreReflective)]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = StrongTypedIdsAreReflective)]
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

        FisherDocumentStorage<TDoc, TId> dirtyTracking = descriptor.ConcurrencyMode switch
        {
            ConcurrencyMode.Optimistic => new OptimisticDirtyTrackingFisherStorage<TDoc, TId>(mapping, descriptor),
            ConcurrencyMode.Numeric => new NumericDirtyTrackingFisherStorage<TDoc, TId>(mapping, descriptor),
            _ => new UnversionedDirtyTrackingFisherStorage<TDoc, TId>(mapping, descriptor)
        };

        // All four flavors are built for every registered type, whether or not any session asks for
        // them: they are cheap, they are cached with the provider, and which one a session resolves is
        // decided per session by SessionOptions.Tracking.
        return new DocumentProvider<TDoc>(queryOnly, lightweight, identityMap, dirtyTracking);
    }
}
