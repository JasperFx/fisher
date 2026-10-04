# Native AOT

Fisher's document storage works in a Native AOT image (`PublishAot=true`): storing, loading, upserting
and LINQ queries, over Guid, string, `int` and `long` identities, strong-typed id wrappers and document
hierarchies. So does the event store: starting and appending to streams, live aggregation,
`FetchForWriting`, inline snapshots, and the async daemon running async snapshots and multi-stream
projections. So do keyset paging (`ToCursorPageAsync`), `Include()`, full-text search with relevance
ordering, and projection step-through. A smoke application exercising all of these is published
natively and run in Fisher's CI on every change.

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
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/native_aot_samples.cs#L79-L97' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_native_aot_configuration' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/native_aot_samples.cs#L66-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_native_aot_json_context' title='Start of snippet'>anchor</a></sup>
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

An aggregate whose identity is a **strong-typed id** needs its id type named when it is registered.
Under the JIT Fisher works the type out by reflection. A native image cannot close the projection over a
type it only meets at runtime, least of all a `readonly record struct`. Register it like this:

<!-- snippet: sample_native_aot_strong_typed_aggregates -->
<a id='snippet-sample_native_aot_strong_typed_aggregates'></a>
```cs
// A snapshotted aggregate keyed on a strong-typed id names the id type too, so its
// projection is closed while both types are still generic arguments.
options.Schema.For<Berth>().Identity(x => x.Id);
options.Projections.Snapshot<Berth, BerthId>(SnapshotLifecycle.Inline);

// One that is only ever aggregated live is declared the same way.
options.Projections.LiveStreamAggregation<Tide, TideId>();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/native_aot_samples.cs#L102-L110' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_native_aot_strong_typed_aggregates' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A strong-typed aggregate that is not registered either way is refused by name in a native image, rather
than failing inside JasperFx. This needs JasperFx 2.80.2 or later
([jasperfx#950](https://github.com/JasperFx/jasperfx/issues/950)).

## Async projections and the daemon

Async projections need nothing extra. Name the projected document types in the JSON context, as you
do for any document. The CI smoke runs the daemon both ways in the native image: built with
`BuildProjectionDaemonAsync()`, and hosted with `AddAsyncDaemon()`. Subscriptions, messages a
projection publishes, and events it raises all work under the hosted daemon, and so does raw SQL
through `AdvancedSql` and `QueueSqlCommand`.

## A second store

`AddFisherStore<T>()` implements the marker interface as a `DispatchProxy`, which emits a type at
runtime and so can't exist in a native image. Declare the store as a class instead:

<!-- snippet: sample_native_aot_second_store_class -->
<a id='snippet-sample_native_aot_second_store_class'></a>
```cs
// A second store as a class rather than a proxy. DocumentStore already implements everything the
// marker inherits, so the class body is empty.
public interface ILedgerStore : IDocumentStore;

public sealed class LedgerStore(StoreOptions options) : DocumentStore(options), ILedgerStore;
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/native_aot_samples.cs#L58-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_native_aot_second_store_class' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and register it with both types:

<!-- snippet: sample_native_aot_second_store -->
<a id='snippet-sample_native_aot_second_store'></a>
```cs
services.AddFisherStore<ILedgerStore, LedgerStore>(options =>
{
    options.Connection("Data Source=ledger.db");
    options.ConfigureSerialization(configure: json => json.TypeInfoResolver = AppJsonContext.Default);
});
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/native_aot_samples.cs#L115-L121' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_native_aot_second_store' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The one-type-argument overload is refused by name in a native image, naming this one. Under the JIT
both work the same way.
