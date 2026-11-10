using Aspire.Hosting.ApplicationModel;

namespace Fisher.Aspire.Hosting;

/// <summary>
///     A Fisher SQLite store, described to an Aspire AppHost as a FILE rather than a server.
///     Its connection string is a SQLite connection string over that file's path, so a project that
///     <c>WithReference</c>s it receives <c>ConnectionStrings__{name}</c> and passes it to
///     <c>AddFisher(connectionString)</c>.
/// </summary>
public class FisherResource(string name, string databasePath) : Resource(name), IResourceWithConnectionString
{
    /// <summary>The path of the SQLite database file, as the AppHost spelled it.</summary>
    public string DatabasePath { get; } = databasePath;

    /// <inheritdoc />
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"Data Source={DatabasePath}");
}
