# Document Identity

Fisher finds a document's identity member by convention — a public member named `Id` or `id` — or by
`[Identity]`. Four types are supported directly, plus a wrapper around any of them.

| Id type | Column | Assigned by |
| :--- | :--- | :--- |
| `Guid` | TEXT, lowercase canonical | Fisher, a version-7 Guid |
| `string` | TEXT | You |
| `int` | INTEGER | Fisher, from a [Hi-Lo sequence](#hi-lo-sequences) |
| `long` | INTEGER | Fisher, from a Hi-Lo sequence |

```cs
public class User
{
    public Guid Id { get; set; }         // assigned on Store if empty
}

public class Country
{
    public string Id { get; set; } = ""; // you assign it
}

public class Invoice
{
    public int Id { get; set; }          // assigned from fi_hilo
}
```

## Guids are lowercase canonical text

SQLite has no Guid type, so Fisher stores the **lowercase canonical form** — `d3f1…`, not `D3F1…`.
SQLite's default collation is case-sensitive, so this matters more than it looks: a Guid bound as a
raw parameter is written UPPERCASE by Microsoft.Data.Sqlite, which would write rows that can never be
read back. Every load returns null and every id match fails, silently, and only for Guid-identified
types.

Fisher converts on every write path it owns. The one place it can reach you is
[raw SQL](/documents/querying/raw-sql), where Fisher converts your parameter for you.

## Hi-Lo sequences

A numeric identity is assigned from a Hi-Lo sequence held in `fi_hilo` — one row per sequence.

<!-- snippet: sample_documents_hilo -->
<a id='snippet-sample_documents_hilo'></a>
```cs
// Per document type. Schema.For<T>() returns an expression; the mapping hangs off it.
opts.Schema.For<Invoice>().Mapping.HiloSettings =
    new Weasel.Core.Sequences.HiloSettings { MaxLo = 100 };

// Or store-wide, for every type with no settings of its own
opts.HiloSequenceDefaults.MaxLo = 100;
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L245-L252' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_hilo' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or declaratively:

```cs
[HiloSequence(MaxLo = 100, SequenceName = "shared")]
public class Invoice { public int Id { get; set; } }
```

Advancing the "hi" is **one atomic statement** — `insert … on conflict … do update set hi_value =
hi_value + 1 returning hi_value` — where Marten calls a stored function and Polecat does a guarded
read-then-update with a retry loop. SQLite's upsert does the whole thing with no window to lose.

::: tip
Sequences are cached by sequence *name*, not by document type, so two types sharing a configured
`SequenceName` share one allocation instead of each holding a private lo range over the same row.
:::

Reset the floor when you need to:

```cs
await store.Advanced.ResetHiloSequenceFloorAsync<Invoice>(10_000);
```

::: warning
`fi_hilo` is created by the sequence itself when needed, not only by the migration. An id is assigned
*inside* `session.Store(document)`, which returns before any commit, so waiting for the commit-time
table creation would be far too late. `AutoCreate.None` is honoured in both places.
:::

## Strong-typed identities

A wrapper struct or class standing in for one of the four types works as both an aggregate's identity
and a document's:

<!-- snippet: sample_documents_strong_typed_id -->
<a id='snippet-sample_documents_strong_typed_id'></a>
```cs
public readonly record struct CatchId(Guid Value);

public class TaggedCatch
{
    public CatchId Id { get; set; }
    public string Species { get; set; } = "";
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L30-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_strong_typed_id' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The shape is JasperFx's, described by `ValueTypeInfo`: one public gettable property, plus a matching
constructor or a static builder.

```cs
// Loading by a wrapper needs both type parameters, which is what keeps it
// unambiguous against the four single-parameter overloads.
var order = await session.LoadAsync<Order, OrderId>(id);
```

::: tip
**Fisher discovers wrappers rather than requiring registration**, which is Polecat's model rather than
Marten's. There is no `RegisterValueType<T>` call to make.
:::

Two things fall out of the design and are worth knowing:

- **The column holds the inner value.** The wrapper exists only in .NET, so the table shape, the write
  SQL and everything downstream are untouched. An `int`-backed wrapper gets an INTEGER column, not a
  TEXT one.
- **A Guid-backed wrapper goes through the same lowercase-canonical conversion as a raw one.** That
  conversion lives in the identity strategy precisely so a wrapper cannot lose it.

Generation mirrors the raw strategies: a version-7 Guid, or the document type's Hi-Lo sequence. A
string-backed wrapper generates nothing, because a raw string key is externally assigned too.

## Overriding the identity member

```cs
public class Report
{
    [Identity]
    public Guid Key { get; set; }
}
```

## Aggregate identity

An aggregate's identity is resolved the same way, and there is one rule worth knowing because it
produces a confusing error otherwise: **conventional `Apply` / `Create` / `ShouldDelete` dispatch is
compile-time only.** JasperFx's source generator emits the dispatcher and keys it on
`(TDoc, TId)`, resolving `TId` from the aggregate's identity member — so an aggregate with no `Id`
gets no dispatcher at all.

Fisher therefore *requires* an identity member and says so, rather than defaulting to the stream
identity primitive and failing later with a message about a missing generated dispatcher.

::: warning
The generator runs in the assembly that **defines the aggregate**, so that project needs a reference
to `JasperFx.Events.SourceGenerator`, and a conventional-method projection class must be declared
`partial`.
:::

`TId` is the aggregate's own id type, not the stream identity primitive. They coincide for a plain
`Guid Id`, but a strong-typed id is a wrapper struct and the generated dispatcher is keyed on the
wrapper.
