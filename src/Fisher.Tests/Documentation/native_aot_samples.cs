using System.Text.Json.Serialization;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/configuration/native-aot.md.
 *
 * This compiles under the JIT like every other sample. Whether it RUNS in a native image is what
 * smoke/aot-consumer checks, with the same configuration.
 */

public readonly record struct CharterId(Guid Value);

public class Charter
{
    public CharterId Id { get; set; }
    public decimal Price { get; set; }
}

public class Vessel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class Trawler : Vessel
{
    public int NetCount { get; set; }
}

#region sample_native_aot_json_context
// Native AOT turns off reflection-based System.Text.Json, so the application supplies a
// source-generated context naming every document type it stores.
[JsonSerializable(typeof(Charter))]
[JsonSerializable(typeof(Vessel))]
[JsonSerializable(typeof(Trawler))]
internal partial class AppJsonContext : JsonSerializerContext;
#endregion

public static class native_aot_samples
{
    public static void configure_for_native_aot(IServiceCollection services)
    {
        #region sample_native_aot_configuration
        services.AddFisher(options =>
        {
            options.Connection("Data Source=app.db");
            options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

            // Required: the application's source-generated JsonSerializerContext.
            options.ConfigureSerialization(configure: json => json.TypeInfoResolver = AppJsonContext.Default);

            // A strong-typed id has to be declared, so Fisher learns its type without reflection.
            options.Schema.For<Charter>().Identity(x => x.Id);

            // Optional: store the wrapper as its primitive. Use the generic overload.
            options.RegisterValueType<CharterId>();

            // A hierarchy's sub-classes have to be registered generically, one by one.
            options.Schema.For<Vessel>().AddSubClass<Trawler>();
        });
        #endregion
    }
}
