# Document Hierarchies

A base type and its sub-classes can share one table and one identity space.

<!-- snippet: sample_documents_hierarchy -->
<a id='snippet-sample_documents_hierarchy'></a>
```cs
public abstract class Vehicle
{
    public Guid Id { get; set; }
    public string Registration { get; set; } = "";
}

public class Car : Vehicle
{
    public int Doors { get; set; }
}

public class Truck : Vehicle
{
    public decimal PayloadTonnes { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L97-L113' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_hierarchy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sample_documents_add_subclass -->
<a id='snippet-sample_documents_add_subclass'></a>
```cs
opts.Schema.For<Vehicle>()
    .AddSubClass<Car>()
    .AddSubClass<Truck>("lorry");     // an explicit alias
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L198-L202' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_add_subclass' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or sweep an assembly:

<!-- snippet: sample_documents_add_subclass_hierarchy -->
<a id='snippet-sample_documents_add_subclass_hierarchy'></a>
```cs
opts.Schema.For<Vehicle>().AddSubClassHierarchy();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L204-L206' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_add_subclass_hierarchy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

```cs
session.Store(new Car { … });                     // lands in fi_doc_vehicle

var vehicle = await session.LoadAsync<Vehicle>(id);      // comes back as Car
var all = await session.Query<Vehicle>().ToListAsync();  // every sub-class, as itself
var cars = await session.Query<Car>().ToListAsync();     // narrowed in SQL
```

## The discriminator is `doc_type`

A short alias in a column of its own — **not** `dotnet_type`.

That is worth stating because `dotnet_type` is already on every row and looks like the obvious
candidate. It is not: it holds an assembly-qualified name, which is long, not worth indexing, and
brittle across an assembly rename. Both siblings keep the columns separate too.

A sub-class's default alias follows the same convention the base type's does — the base's
discriminator alias *is* the alias its table is named from — so a sub-class spelled differently would
put two conventions in one column.

::: tip
Name the alias explicitly if the type may be renamed. The alias is what is *stored*.
:::

## A sub-class never gets a mapping of its own

That is the whole point, and it is enforced before the mapping cache rather than after. Without the
check, `Store(derived)` would create a mapping and write to `fi_doc_car`: the sub-class is
registered, carries an alias, and still lands in the wrong table.

## The two narrowing paths are different on purpose

| Read | Narrowed |
| :--- | :--- |
| `Query<TDerived>()` | in SQL, with a `doc_type` predicate |
| `LoadAsync<TDerived>(id)` | in memory, by testing what came back |

A load names one row and the id is unique across the hierarchy, so a discriminator predicate would
only turn "that id is a different sub-class" into the same answer as "no such id".

## The query filter is one statement-level pass

Not composed into each caller predicate. Two ways to get this wrong, and both were hit during
development:

- Composing it per predicate repeats it *and omits it entirely from a query with none*.
- Hanging it off the soft-delete branch omits it for a type that is not soft-deleted, and for the
  `IsDeleted` / `MaybeDeleted` scopes of one that is.

It is an `in` over the aliases **at or below** the queried type rather than an equality, because a
sub-class may have sub-classes. Polecat emits a bare equality, which is correct only two levels deep.

## An unknown alias throws

::: warning
A row whose `doc_type` this deployment does not recognise **throws** rather than falling back to the
base. A row written by a deployment that knew a sub-class this one does not is a real configuration
gap; deserializing it as the base hands back an object quietly missing whatever the sub-class added.
:::

This is deliberately the **opposite** of the event reads' policy, which skip an unresolvable
`dotnet_type` — an event store must stay readable by a deployment that does not know every event,
where a document load has one right answer.

## AddSubClassHierarchy

<!-- snippet: sample_documents_add_subclass_hierarchy -->
<a id='snippet-sample_documents_add_subclass_hierarchy'></a>
```cs
opts.Schema.For<Vehicle>().AddSubClassHierarchy();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L204-L206' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_add_subclass_hierarchy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

An overload takes the assembly to sweep, where the no-argument form uses the calling one.

::: tip
It orders by **full name, not by reflection order**. Two sub-classes whose default aliases collide
have to fail the same way on every run, and `Assembly.GetTypes()` promises no ordering — a collision
that appeared on one machine and not another would be the worst version of that error.
:::

Abstract and interface types are skipped, because a discriminator names something a row can be read
back *as*.

## An abstract or interface base

An abstract or interface base is a hierarchy whether or not anything is registered, so its table
carries the `doc_type` column from the **first** migration. Adding it later would leave the rows
already written with no discriminator to read.

## Hierarchies elsewhere

- A **joined** hierarchy comes back as its real sub-classes, because the inner document is
  materialized by its own storage's selector. See [Joins](/documents/querying/linq/joins).
- **Raw SQL** does too, for the same reason. See [Raw SQL](/documents/querying/raw-sql).
- **Ejecting** a hierarchy works without knowing it is one: the map is keyed by the *base*, so
  `EjectAllOfType` scans entries whose key is not exactly the type and removes matching values
  individually.
