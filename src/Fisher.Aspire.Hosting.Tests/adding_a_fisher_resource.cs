using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Fisher.Aspire.Hosting;

namespace Fisher.Aspire.Hosting.Tests;

public class adding_a_fisher_resource
{
    [Fact]
    public async Task the_connection_string_is_the_file_path()
    {
        var builder = DistributedApplication.CreateBuilder();
        var fisher = builder.AddFisher("orders", "/data/orders.db");

        var cs = await fisher.Resource.ConnectionStringExpression.GetValueAsync(
            CancellationToken.None);

        cs.ShouldBe($"Data Source={Path.GetFullPath("/data/orders.db")}");
        fisher.Resource.Name.ShouldBe("orders");
    }

    [Fact]
    public void the_default_path_is_named_after_the_resource()
    {
        var builder = DistributedApplication.CreateBuilder();
        var fisher = builder.AddFisher("orders");

        fisher.Resource.DatabasePath.ShouldBe(Path.GetFullPath("orders.db"));
    }

    [Fact]
    public async Task with_volume_sets_the_data_directory_environment()
    {
        var builder = DistributedApplication.CreateBuilder();
        var fisher = builder.AddFisher("orders", "/data/orders.db");
        var container = builder.AddContainer("svc", "img").WithVolume(fisher);

        var env = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(
            DistributedApplicationOperation.Run), env);
        foreach (var a in container.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await a.Callback(context);

        env[FisherResourceBuilderExtensions.VolumeEnvironmentVariable]
            .ShouldBe(Path.GetFullPath("/data"));
    }
}
