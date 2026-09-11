# Full-Text Search

Fisher searches documents through SQLite's [FTS5](https://www.sqlite.org/fts5.html). Declare an index
over the members you want searchable:

<!-- snippet: sample_full_text_declare_index -->
<a id='snippet-sample_full_text_declare_index'></a>
```cs
opts.Schema.For<SearchableArticle>().FullTextIndex(x => x.Title, x => x.Body);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/full_text_samples.cs#L24-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_full_text_declare_index' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and search it:

<!-- snippet: sample_full_text_search -->
<a id='snippet-sample_full_text_search'></a>
```cs
var articles = await session.Query<SearchableArticle>()
    .Where(x => x.PlainTextSearch("quick brown fox"))
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/full_text_samples.cs#L39-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_full_text_search' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

FTS5 ships in the `SQLitePCLRaw.bundle_e_sqlite3` build Fisher already depends on, so there is nothing
to install and no extension to load.

## How the index is kept in step

The index is an **external-content FTS5 virtual table kept in sync by three database triggers** — one
each for insert, update and delete on the document table.

The alternative was maintaining it on Fisher's own write path, which is less machinery: no view, no
triggers, nothing new in the migration. It was rejected because of how it fails. A write that does not
go through Fisher's upsert — a `QueueSqlCommand`, an `ITransactionParticipant`'s own statement, an EF
Core context sharing the file, a `sqlite3` shell, a restored backup edited in place — would leave the
index behind, and a stale full-text index does not error. It returns fewer rows than it should, which
is indistinguishable from a search that legitimately matched nothing.

A trigger is a database-level object, so it fires for **every** writer on the file. What it costs is
three schema objects per indexed type and one extra write per document write; what it buys is that the
only way to desync the index is to drop the triggers.

It also happened to be the cheaper option here, which is worth saying because it usually is not:
`Weasel.Sqlite` already ships `Trigger` and `View` as first-class schema objects with delta detection,
so all of it goes through the ordinary migration with no bespoke plumbing.

Four objects per indexed document type, all named `fi_fts_*` rather than `fi_doc_*`:

| Object | Name | Job |
| :--- | :--- | :--- |
| View | `fi_fts_article_src` | The `json_extract` expressions, named as the index's columns |
| Virtual table | `fi_fts_article` | The FTS5 index itself |
| Triggers | `fi_fts_article_ai` / `_ad` / `_au` | Keep it in step |

::: tip
The `fi_fts_` prefix is a correctness rule, not a preference. An FTS5 virtual table brings four shadow
tables of its own, and `IDocumentCleaner.DeleteAllDocumentsAsync` discovers its targets by the
`fi_doc_` prefix — so under the obvious name it would issue `delete from` against the index and every
shadow of it. A `DELETE` on an external-content FTS5 table does not error.
:::

**The index stores terms, not text.** External content means FTS5 keeps no copy of the indexed strings,
so an index costs the inverted index alone — which matters more here than on either sibling, where the
store is not usually the application's own disk footprint.

## Migration and rebuild

The index participates in Fisher's schema migration like everything else: `AutoCreate.None`,
`ApplyAllConfiguredChangesToDatabaseAsync`, `db-apply`, `db-assert`, `db-patch` and `db-dump` all mean
here what they mean everywhere.

**An index created over a table that already has rows would start empty**, because the triggers only
fire on writes made after they exist. So the `CREATE VIRTUAL TABLE` is followed by FTS5's own
`'rebuild'` command in the same migration — declaring an index on a store that already holds documents
populates it as part of creating it. That is what the content view is for: FTS5 can only repopulate
itself from a source whose column names match its own, and a document table has `id` and `data` and
nothing called `title`.

Two operations for the cases triggers cannot cover:

<!-- snippet: sample_full_text_maintenance -->
<a id='snippet-sample_full_text_maintenance'></a>
```cs
await store.Advanced.CheckFullTextIndexAsync<SearchableArticle>();
await store.Advanced.RebuildFullTextIndexAsync<SearchableArticle>();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/full_text_samples.cs#L62-L65' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_full_text_maintenance' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Neither is needed in the ordinary course of things. Reach for them after a writer that ran with the
triggers absent — a bulk load into a copy of the file, a restore — or after a **table rebuild**: SQLite
cannot alter most of a table, so Weasel recreates it and copies the rows, which reassigns rowids, and
the index is keyed on rowid. Weasel re-emits the triggers it dropped, so writes from then on are fine;
the rows copied across are not.

`CheckFullTextIndexAsync` exists beside the repair rather than only the repair because a stale index
does not error — without it there is no way to ask the question at all.

## The operators

All six are called on the **document**, not on a member, because the index covers the members its
declaration named and the query does not get to pick among them:

| Operator | Means | Safe for raw user input |
| :--- | :--- | :--- |
| `Search(term)` | FTS5 query syntax, passed through | no — a malformed query is an error |
| `PlainTextSearch(term)` | every word, any order | yes |
| `PhraseSearch(term)` | the words adjacent and in order | yes |
| `WebStyleSearch(term)` | quoted phrases, `or`, `-` to exclude | yes |
| `PrefixSearch(term)` | each word as a prefix | yes |
| `NgramSearch(term)` | anywhere inside a word | yes — needs a `Trigram` index |

Everything but `Search` quotes each term before it reaches FTS5, so a search for `OR`, `*` or a
quotation mark is a search rather than a reinterpreted query. `Search` documents the opposite: its
syntax is yours to get right, and that is what it is for.

```cs
.Where(x => x.Search("quick AND fox"))    // boolean
.Where(x => x.Search("\"quick brown\""))  // phrase
.Where(x => x.Search("title: fox"))       // one indexed column
.Where(x => x.Search("NEAR(lazy dog)"))
```

A full-text predicate is an ordinary `where` fragment, so it composes with everything else — other
predicates, `!`, ordering, paging, the aggregate terminals — and the tenancy, soft-delete and
hierarchy filters apply to it exactly as they apply to any other query. A soft-deleted document is not
returned by a search.

<!-- snippet: sample_full_text_composes -->
<a id='snippet-sample_full_text_composes'></a>
```cs
var page = await session.Query<SearchableArticle>()
    .Where(x => x.PlainTextSearch("wombat") && x.Author == "Aesop")
    .OrderBy(x => x.Title)
    .ToPagedListAsync(1, 20);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/full_text_samples.cs#L50-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_full_text_composes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Tokenizers

The tokenizer is fixed when the index is created and decides which searches can possibly match.

| `FullTextTokenizer` | Splits on | Stems | For |
| :--- | :--- | :--- | :--- |
| `Porter` (default) | words, diacritics folded | yes | prose |
| `Unicode` | words, diacritics folded | no | identifiers, tags, non-English text |
| `Trigram` | three-character windows | no | substring search |

<!-- snippet: sample_full_text_trigram_index -->
<a id='snippet-sample_full_text_trigram_index'></a>
```cs
opts.Schema.For<SearchableArticle>()
    .FullTextIndex(FullTextTokenizer.Trigram, x => x.Title);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/full_text_samples.cs#L31-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_full_text_trigram_index' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A `Trigram` index is what `NgramSearch` needs, and it is the only thing that can serve it — a word
tokenizer stores whole terms and physically cannot match a fragment of one. So the two are tied
together and mismatches are refused by name in both directions:

- `NgramSearch` against a `Porter` or `Unicode` index is refused;
- `Search` / `PlainTextSearch` / `PhraseSearch` / `WebStyleSearch` / `PrefixSearch` against a
  `Trigram` index are refused, because they would match only where a word happened to be exactly
  three characters long.

## Indexing the whole document

Naming no member indexes the stored JSON:

```cs
opts.Schema.For<Note>().FullTextIndex();
```

Marten's member-less `FullTextIndex()` does the same thing over `data::text`, and it has the same
consequence: the JSON's **key names** become matchable terms, so a document with a `title` property
makes `title` a hit. That is noise rather than wrongness, and naming the members is how you avoid it.

## The attribute

```cs
public class Recipe
{
    public Guid Id { get; set; }

    [FullTextIndex] public string Name { get; set; } = "";
    [FullTextIndex] public string Method { get; set; } = "";

    public string Notes { get; set; } = "";   // not indexed
}
```

On the type instead of on members, `[FullTextIndex]` indexes the whole document. Both together is
refused — the type-level form widens to everything and the members would narrow the very thing it
widened — and so are two member attributes naming different tokenizers, since the index has exactly
one.

## One index per document type

Fisher supports **one full-text index per document type**, and a second declaration is refused by
name. Marten permits several and carries an `AmbiguousFullTextIndexException` for the case where a
search cannot tell which one it meant; Fisher takes the other branch, because a search operator names
no index and so with one there is nothing to disambiguate. Put every searchable member in the one
declaration.

## Relevance ordering

A search is a predicate, so on its own it says only whether a document matched. To put the best match
first, order by relevance:

<!-- snippet: sample_full_text_relevance -->
<a id='snippet-sample_full_text_relevance'></a>
```cs
var best = await session.Query<SearchableArticle>()
    .Where(x => x.PlainTextSearch("quick brown fox"))
    .OrderByRelevance()
    .Take(10)
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/full_text_samples.cs#L70-L76' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_full_text_relevance' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`OrderByRelevance` is FTS5's `bm25()` over the index the query's predicate searched. It reads
best-first because that is what it does: `bm25()` scores a better match more *negative*, so a raw
ascending sort is already the right way round and `OrderByRelevanceDescending` is the rare worst-first
case, named so nobody has to discover the sign.

It is an ordering term like any other, so it composes. `OrderByRelevance().ThenByDescending(x =>
x.Published)` breaks ties by date; `OrderBy(x => x.Category).ThenByRelevance()` ranks within each
category; paging, `Count()` and an ordinary predicate in the same `Where` all still work. All four
members of the family are there: `OrderByRelevance`, `OrderByRelevanceDescending`, `ThenByRelevance`
and `ThenByRelevanceDescending`.

Marten's counterpart is `OrderByTextRank(term, TextSearchFunction)`, which repeats the search term
and names the query function because PostgreSQL's `ts_rank` needs both again. FTS5's `bm25()` reads
its match from the `MATCH` it is attached to, so Fisher's takes neither.

### Column weights

`bm25()` weights every indexed column at 1.0 by default. Pass one weight per column, **in the order the
index declared its members**, to change that:

<!-- snippet: sample_full_text_relevance_weights -->
<a id='snippet-sample_full_text_relevance_weights'></a>
```cs
// The index declared Title then Body, so a Title hit counts for ten Body hits
var best = await session.Query<SearchableArticle>()
    .Where(x => x.PlainTextSearch("wombat"))
    .OrderByRelevance(10.0, 1.0)
    .ThenByDescending(x => x.Author)
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/full_text_samples.cs#L83-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_full_text_relevance_weights' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Marten expresses the same idea at index-declaration time with `WeightedFullTextIndex` and its A–D
weight classes; FTS5 puts it on the query, so it is an argument here and a query can weight the same
index differently from another. Supplying a different number of weights than the index has columns
is refused rather than padded.

## Snippets and highlights

Two projections read text *out of* the match. Both are only meaningful inside a `Select` on a query
that carries a full-text predicate, because the value is computed by the match and nothing on the
document holds it.

`Snippet()` is FTS5's `snippet()`: the best-matching fragment, with the matched terms marked and the
rest elided.

<!-- snippet: sample_full_text_snippet -->
<a id='snippet-sample_full_text_snippet'></a>
```cs
var hits = await session.Query<SearchableArticle>()
    .Where(x => x.PlainTextSearch("corrosion"))
    .OrderByRelevance()
    .Select(x => new { x.Id, x.Title, Extract = x.Snippet() })
    .ToListAsync();

// Extract: "…the <b>corrosion</b> on the lower hull was…"
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/full_text_samples.cs#L97-L105' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_full_text_snippet' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

It picks whichever indexed column matched best. The defaults are `<b>`/`</b>` around each matched
term, `…` where text was cut, and a 32-token budget; the second overload takes all four, and FTS5 caps
the budget at 64 tokens:

<!-- snippet: sample_full_text_snippet_markers -->
<a id='snippet-sample_full_text_snippet_markers'></a>
```cs
var hits = await session.Query<SearchableArticle>()
    .Where(x => x.PlainTextSearch("corrosion"))
    .Select(x => new { x.Id, Extract = x.Snippet("[", "]", " … ", 16) })
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/full_text_samples.cs#L112-L117' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_full_text_snippet_markers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Highlight(column)` is FTS5's `highlight()`: one indexed column **in full**, with the matched terms
marked. The column is named as the member is, and it is required:

<!-- snippet: sample_full_text_highlight -->
<a id='snippet-sample_full_text_highlight'></a>
```cs
var hits = await session.Query<SearchableArticle>()
    .Where(x => x.PlainTextSearch("corrosion"))
    .Select(x => new { x.Id, Title = x.Highlight("Title", "<mark>", "</mark>") })
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/full_text_samples.cs#L124-L129' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_full_text_highlight' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

It is required because FTS5's own "whichever column matched" sentinel returns an empty string from
`highlight()` rather than an error, and "the first column" would be silently wrong whenever the match
was elsewhere. A column the index does not have is refused with the indexed members named.

Both project alongside ordinary members, both compose with relevance ordering, and a marker
containing a quote is escaped rather than spliced into the SQL.

## What is refused

| | Why |
| :--- | :--- |
| Any operator against a type with no declared index | There is nothing to search, and the query would otherwise be valid SQL against a table that does not exist |
| `NgramSearch` against a non-`Trigram` index | A word tokenizer cannot match a fragment of a term, so the search would come back empty rather than failing |
| A word operator against a `Trigram` index | It would match only where a word happened to be three characters long |
| `NgramSearch` with a term under three characters | Shorter than the windows a trigram index stores |
| `WebStyleSearch` with only exclusions | FTS5's `NOT` narrows a result set rather than negating one, so there is nothing to narrow |
| A second, different index on one type | A search operator names no index |
| An operator called on anything but the query's own parameter | The search would silently run against the queried type's index instead |
| `OrderByRelevance` without a full-text predicate, or against a negated one, or with two matches in the query | `bm25()` is only legal where its table is the subject of exactly one `MATCH`; anything else is a SQLite error a long way from the call site, or a rank over the wrong match |
| Column weights that do not number the index's columns | Padding or truncating would weight the wrong member silently |
| `Snippet()` or `Highlight()` outside a `Select`, or without a full-text predicate | There is no match to read a value from |
| `Highlight()` naming a member the index does not cover | FTS5 would answer with an empty string, which reads as "no match" |

Every one of them throws `BadLinqExpressionException` naming the operator. Each replaces an answer
that would have been an empty result — which for a search is indistinguishable from success.

## Not supported

- **No full-text index on an event body.** `QueryEventDataAsync` searches event bodies with the
  ordinary string operators; the index is a document-storage feature.
- **No `OrderByNgramRank`.** Marten ranks trigram matches with its own similarity function; FTS5's
  `bm25()` runs over a `Trigram` index like any other, so `OrderByRelevance` after `NgramSearch` is the
  equivalent and there is no separate operator.
