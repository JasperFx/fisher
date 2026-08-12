# Searching on String Fields

```cs
.Where(x => x.Name == "Frodo")
.Where(x => x.Name.Contains("rod"))
.Where(x => x.Name.StartsWith("Fro"))
.Where(x => x.Name.EndsWith("do"))
```

All four are **ordinal and case-sensitive**, matching what .NET's own `string.Contains(string)` means.

## Why not LIKE?

This is a real divergence from both siblings, and the reason is worth knowing because the alternative
would be a query surface that contradicts itself.

**SQLite's `LIKE` is case-*insensitive* for ASCII, while `=` is case-*sensitive*.** So a LIKE-based
`Contains("frodo")` would match `"Frodo"` on data where `== "frodo"` does not — two operators in one
`Where` clause disagreeing about what a string is.

Fisher uses `instr` and `substr` instead:

| Operator | SQL |
| :--- | :--- |
| `Contains` | `instr(json_extract(data, '$.name'), ?) > 0` |
| `StartsWith` | `substr(json_extract(data, '$.name'), 1, length(?)) = ?` |
| `EndsWith` | `substr(json_extract(data, '$.name'), -length(?)) = ?` |

There is a second reason: `_` and `%` are `LIKE` wildcards, so a LIKE-based implementation needs an
`ESCAPE` clause for user input containing either. Polecat's `[_]` bracket form is T-SQL-only and does
not exist here.

::: tip
This is the same trap the document cleaner hits from the other side — `_` is a single-character
wildcard, so `like 'fi_%'` would happily match a table called `fixtures`. Fisher matches table names
in C# with `StartsWith` for exactly that reason.
:::

## Case-insensitive searching

There is no `StringComparison` overload, because SQLite has no ordinal-ignore-case collation Fisher
could rely on across every build. If you need case-insensitive matching, normalise on write:

```cs
public class User
{
    public string Name { get; set; } = "";
    public string NameLower => Name.ToLowerInvariant();
}
```

```cs
.Where(x => x.NameLower.Contains(term.ToLowerInvariant()))
```

A [duplicated field](/documents/indexing/duplicated-fields) or an
[index](/documents/indexing/indexes) over the normalised member then makes it a range scan.

## Making a string search fast

`instr(...) > 0` is a scan whatever the index situation, the same as `LIKE '%x%'` would be. What an
index *can* serve is a prefix match and an equality:

```cs
opts.Schema.For<User>().Index(x => x.Name);
```

```cs
.Where(x => x.Name == term)          // uses the index
.Where(x => x.Name.StartsWith(term)) // may use the index
.Where(x => x.Name.Contains(term))   // scan
```

::: warning
SQLite's planner uses an expression index only when the query's expression **matches the index's
exactly**. Fisher builds both from the same member locator, which is what makes that true — an index
built from a hand-written `json_extract` would be created without error, never used, and report
nothing.
:::

## Null handling

```cs
.Where(x => x.Name == null)      // json_extract(...) is null
.Where(x => x.Name != null)
```

`json_extract` yields SQL NULL for an absent key, so "the member is null" and "the key is not there"
are the same answer. That is usually what you want, and it is why a null test is built from the raw
locator rather than a normalised one.
