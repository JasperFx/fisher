using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/configuration/hostbuilder.md and
 * docs/configuration/multiple-stores.md.
 *
 * See "Documentation samples" in CLAUDE.md: every sample a reader would copy lives in a
 * #region here and is pulled into the markdown by mdsnippets, so a sample that stops compiling
 * fails the build rather than going stale in a page nobody rebuilds.
 */

public interface IReportingStore : IDocumentStore;

public class Report
{
    public Guid Id { get; set; }
    public DateTimeOffset RunAt { get; set; }
}

public class SalesProjection : Fisher.Projections.SingleStreamProjection<Report, Guid>;

public static class configuration_samples
{
    public static void configure_fisher_lambda(IServiceCollection services)
    {
        #region sample_configure_fisher_lambda
        // Layered onto whatever store the application configured, either side of the AddFisher call.
        services.ConfigureFisher(options =>
        {
            options.Schema.For<Report>().Duplicate(x => x.RunAt);
            options.Projections.Snapshot<Report>(SnapshotLifecycle.Async);
        });

        // The overload taking the container as well, for configuration that needs a resolved service.
        services.ConfigureFisher((serviceProvider, options) =>
        {
            options.Projections.Add(
                serviceProvider.GetRequiredService<SalesProjection>(), ProjectionLifecycle.Async);
        });
        #endregion
    }

    public static void configure_fisher_lambda_targeted(IServiceCollection services)
    {
        #region sample_configure_fisher_lambda_targeted
        // Reaches the store registered as IReportingStore, and no other.
        services.ConfigureFisher<IReportingStore>(options =>
            options.Projections.Add(new SalesProjection(), ProjectionLifecycle.Async));

        services.ConfigureFisher<IReportingStore>((serviceProvider, options) =>
            options.Projections.Add(
                serviceProvider.GetRequiredService<SalesProjection>(), ProjectionLifecycle.Async));
        #endregion
    }

    public static void event_model_name(IServiceCollection services, string connectionString)
    {
        #region sample_event_model_name
        // Slices are grouped by MODEL NAME before they are merged, so a host that names its own model
        // has to name the store's source to match -- otherwise the store's View slices assemble a
        // second model called "EventModel" beside the host's, and neither canvas carries both halves.
        services.AddFisher(options =>
        {
            options.ConnectionString = connectionString;
            options.Projections.Snapshot<Report>(SnapshotLifecycle.Inline);
        }, eventModelName: "Storefront");

        // An ancillary store has to be told separately: it can be registered with no AddFisher at all,
        // so there is no primary registration for it to read the name off.
        services.AddFisherStore<IReportingStore>(options =>
        {
            options.ConnectionString = connectionString;
            options.Projections.Snapshot<Report>(SnapshotLifecycle.Inline);
        }, eventModelName: "Storefront");
        #endregion
    }
}
