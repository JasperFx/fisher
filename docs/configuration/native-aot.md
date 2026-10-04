# Native AOT

Fisher's document storage works in a Native AOT image (`PublishAot=true`): storing, loading, upserting
and LINQ queries, over Guid, string, `int` and `long` identities, strong-typed id wrappers and document
hierarchies. So does the core of the event store: starting and appending to streams, live aggregation,
`FetchForWriting`, and an inline `Snapshot<T>`. A smoke application is published natively and run in
Fisher's CI on every change.

Three things are different from a JIT application. Each has to be configured, because the reflection
that works them out under the JIT isn't available in a native image.

<!-- snippet: sample_native_aot_configuration -->
<a id='snippet-sample_native_aot_configuration'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/native_aot_samples.cs#L46-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_native_aot_configuration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Supply a source-generated JSON context

Native AOT disables reflection-based System.Text.Json. Without a context, the first write fails with
*"Reflection-based serialization has been disabled for this application"*. Name every document type
you store:

<!-- snippet: sample_native_aot_json_context -->
<a id='snippet-sample_native_aot_json_context'></a>
```cs
// Native AOT turns off reflection-based System.Text.Json, so the application supplies a
// source-generated context naming every document type it stores.
[JsonSerializable(typeof(Charter))]
[JsonSerializable(typeof(Vessel))]
[JsonSerializable(typeof(Trawler))]
internal partial class AppJsonContext : JsonSerializerContext;
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/native_aot_samples.cs#L33-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_native_aot_json_context' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The context also keeps each document's properties from being trimmed, which is how Fisher finds the
`Id` member. If a native image reports that a document *"has no identity member"* when it plainly has
one, the type is missing from the context. Under Native AOT the message says so.

## Declare strong-typed identities

Under the JIT, Fisher finds a wrapper like `CharterId` by convention. In a native image it needs the
wrapper named as a generic argument, so declare the identity with `Schema.For<T>().Identity(x => x.Id)`.
An undeclared wrapper fails with a message naming that call.

`RegisterValueType<T>()`, which stores the wrapper as its primitive, works too. Use the generic
overload: `RegisterValueType(Type)` is refused under Native AOT.

## Register sub-classes generically

Register each sub-class with `AddSubClass<TSub>()`. `AddSubClass(Type)` and `AddSubClassHierarchy()`
only know the base type at runtime, so they are refused under Native AOT, with a message naming the
generic call.

## Events and aggregates

Name every event type and every aggregate in the same JSON context as your documents. A conventional
aggregate's `Apply`/`Create` dispatch is source-generated, so nothing about it needs reflection. A
project that references Fisher as a package gets the generator with it.

An aggregate whose identity is a **strong-typed id** does not work in a native image yet. JasperFx
compiles the wrapper's accessors with FastExpressionCompiler, which throws there
([jasperfx#942](https://github.com/JasperFx/jasperfx/issues/942)). Fisher refuses such an aggregate by
name rather than failing inside JasperFx. Key it on a Guid, string, `int` or `long` until that ships.

::: warning What has not been measured
The CI smoke covers document storage and the event-store paths above, through `AddFisher`. Async
projections, the async daemon, and the LINQ operators that still serialize through reflection (cursor
paging, `Include`, full-text extracts) have not been run in a native image.
:::
