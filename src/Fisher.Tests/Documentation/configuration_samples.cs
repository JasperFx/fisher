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
}
