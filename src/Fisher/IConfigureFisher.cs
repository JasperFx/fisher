namespace Fisher;

/// <summary>
///     Configuration applied to a store's options after the container is built, for settings that
///     depend on other services (fisher#46).
/// </summary>
/// <remarks>
///     <para>
///         Registered like any other service — <c>services.AddSingleton&lt;IConfigureFisher,
///         MyConfiguration&gt;()</c> — and run in registration order, after the
///         <c>AddFisher(...)</c> lambda. So the lambda states the store's shape and these adjust it
///         with something only the container knows.
///     </para>
///     <para>
///         <b>The <c>AddFisher(Func&lt;IServiceProvider, StoreOptions&gt;)</c> overload already covers
///         the simple case</b>, and is the better answer when one registration needs one service. This
///         exists for the case that overload cannot express: configuration contributed by a library
///         that does not own the <c>AddFisher</c> call — a package that wants to register its own
///         projections onto whatever store the application configured.
///     </para>
/// </remarks>
public interface IConfigureFisher
{
    /// <summary>Adjust the store's options.</summary>
    void Configure(IServiceProvider services, StoreOptions options);
}

/// <summary>
///     <see cref="IConfigureFisher" /> for one secondary store, identified by its marker interface.
/// </summary>
/// <remarks>
///     Without the type parameter, a contribution registered by one library would reach every store in
///     the application — including ones it has never heard of. Which store a piece of configuration is
///     about has to be sayable.
/// </remarks>
public interface IConfigureFisher<T> : IConfigureFisher where T : IDocumentStore
{
}
