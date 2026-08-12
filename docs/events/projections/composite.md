# Composite Projections

Several projections as **ordered stages under one shard**, rebuilt together in one pass.

```cs
opts.Projections.CompositeProjectionFor("reporting", composite =>
{
    composite.Add(new OrderSummaryProjection());
    composite.Add(new SalesByCustomer());
    composite.Add(new RegionalRollup());
});
```

## What a composite buys, precisely

**Ordered execution in one batch, and one rebuild pass.** That is the whole of it, and the boundary is
worth stating because the natural assumption is wrong:

::: danger
**A later stage cannot read an earlier stage's writes with `LoadAsync`.** The whole composite commits
as one batch, so nothing an earlier stage queued is in the database yet.

JasperFx's mechanism for sharing across stages is the **aggregate cache**, which aggregation
projections participate in and a bare `IProjection` does not — the cache is compacted once at the
composite boundary rather than per stage, precisely so downstream stages can read upstream in-flight
entities.
:::

## Composites are always asynchronous

A stage boundary only means something inside a daemon batch. An inline composite would be a boundary
with nothing on either side of it.

## Event type registration

The child projections' event types are registered on the event graph by `CompositeProjectionFor`, not
by each child.

::: tip
A child inside a composite is never registered on its own, and would otherwise contribute nothing to
what the store knows how to deserialize.
:::

## Rebuild teardown

This is the part with a sharp edge, and it is the same edge flat tables and EF Core-backed projections
each have:

::: danger
**A member held by the composite has to be asked what it publishes**, or a rebuild replays onto its
surviving rows while the progression rows are deleted anyway.

A projection deriving from `ProjectionBase` has its options and published types adopted
automatically. A **raw `IProjection` that is not a `ProjectionBase` declares nothing, and the composite
cannot invent it** — say so explicitly:

```cs
composite.Add(projection, options => options.DeleteViewTypeOnTeardown<MyView>());
```
:::

The composite's **own** options are its own, so this works too:

```cs
opts.Projections.CompositeProjectionFor("reporting", composite =>
{
    composite.Options.DeleteViewTypeOnTeardown<ExtraView>();
    composite.Add(new OrderSummaryProjection());
});
```

::: warning
`Name` and `Version` are deliberately **not** adopted from a member. They compose the member's shard
identity, and changing them orphans every progression row already written.
:::

::: warning
**An ordinary rebuild test cannot catch any of this.** A replay rewrites every row it can still
produce, so a surviving row is invisible except where the replay *cannot* recreate it. Plant one
against an id no event mentions.
:::

## Document mappings

A document a bare `IProjection` stores still needs a registered mapping, since Fisher only creates
tables for types the schema has mapped. That is the ordinary on-demand rule, not a composite quirk.

## When to use one

- Two projections must run in a fixed order over each batch.
- Several projections should rebuild together, so they are never mutually inconsistent.
- You want one shard's worth of progression bookkeeping rather than N.

When the stages are genuinely independent, separate projections are simpler — and they can then fall
behind independently, which is usually a feature.

## What it does not give you

- A transaction boundary between stages. There is one transaction.
- Database visibility between stages. See above.
- Any inline behaviour.
