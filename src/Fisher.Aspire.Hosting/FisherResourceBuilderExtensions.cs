using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Fisher.Aspire.Hosting;

/// <summary>AppHost extensions for describing a Fisher store.</summary>
public static class FisherResourceBuilderExtensions
{
    /// <summary>The environment variable <see cref="WithVolume"/> sets on a referencing project.</summary>
    public const string VolumeEnvironmentVariable = "FISHER_DATA_DIRECTORY";

    /// <summary>
    ///     Adds a Fisher store whose database file is <c>{name}.db</c> in the AppHost's current
    ///     directory, or at <paramref name="databasePath" /> when given. SQLite is an embedded file,
    ///     so nothing is started: the resource exists to carry the connection string to projects.
    /// </summary>
    public static IResourceBuilder<FisherResource> AddFisher(this IDistributedApplicationBuilder builder,
        string name, string? databasePath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var path = Path.GetFullPath(databasePath ?? $"{name}.db");
        return builder.AddResource(new FisherResource(name, path));
    }

    /// <summary>
    ///     Tells a referencing project which directory holds the store's file, for a container whose
    ///     data lives on a mounted volume. Sets <see cref="VolumeEnvironmentVariable" /> on the
    ///     project; the connection string still carries the file path.
    /// </summary>
    public static IResourceBuilder<TDestination> WithVolume<TDestination>(
        this IResourceBuilder<TDestination> builder, IResourceBuilder<FisherResource> fisher)
        where TDestination : IResourceWithEnvironment
    {
        var directory = Path.GetDirectoryName(fisher.Resource.DatabasePath)!;
        return builder.WithEnvironment(VolumeEnvironmentVariable, directory);
    }
}
