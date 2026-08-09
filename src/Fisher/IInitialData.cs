namespace Fisher;

/// <summary>
///     Data seeded into the store at startup (fisher#39).
/// </summary>
/// <remarks>
///     <para>
///         Registered on <see cref="StoreOptions.InitialData" /> and run by <c>AddFisher</c>'s hosted
///         services, <b>after</b> the schema has been applied — an ordering that matters and is not
///         incidental: a seeder writing to a table that does not exist yet would fail, and
///         <c>ApplyAllDatabaseChangesOnStartup()</c> is the only thing that creates them.
///     </para>
///     <para>
///         <b>Implementers are responsible for not duplicating data across restarts.</b> Fisher keeps
///         no "already seeded" marker, and inventing one would be a table nobody asked for holding a
///         claim Fisher cannot verify — a seeder that upserts by a known id is idempotent for free,
///         which is what every useful seeder does anyway. Marten and Polecat say the same.
///     </para>
///     <para>
///         The interface is <c>JasperFx.IInitialData&lt;IDocumentStore&gt;</c> closed over Fisher's
///         store; this marker exists so the name is <c>Fisher.IInitialData</c> and a seeder ports
///         between the stores with only its namespace changed.
///     </para>
/// </remarks>
public interface IInitialData : JasperFx.IInitialData<IDocumentStore>
{
}

/// <summary>
///     The seeders a store runs at startup, in the order they were added.
/// </summary>
/// <remarks>
///     Inherits JasperFx's lifted collection, which carries the lambda <c>Add</c> overload — so a
///     one-line seeder needs no class.
/// </remarks>
public class InitialDataCollection : JasperFx.InitialDataCollection<IDocumentStore>
{
}
