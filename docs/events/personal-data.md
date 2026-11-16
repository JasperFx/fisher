# Personal Data and GDPR Erasure

An event store keeps everything by design. A request under GDPR Article 17, the right to erasure,
asks for the opposite. The usual answer in an event-sourced system is **crypto-shredding**:

- personal data is encrypted with a key that belongs to one data subject, before it is stored;
- erasing that person means deleting their key;
- the ciphertext can stay where it is, because nothing can read it any more.

Events stay immutable and streams stay intact. The data is unreadable everywhere a copy of it went,
including projections, messages and backups, without anything having to find those copies.

## Tayra is the Critter Stack's answer

[Tayra](https://tayra.dev) is the officially sanctioned crypto-shredding solution for the Critter
Stack, from Radarleaf Technologies and Babu Annamalai, a Critter Stack co-maintainer. It provides:

- **field-level encryption** of personal data, marked declaratively on your types;
- **HMAC blind indexes**, so encrypted fields can still be queried by exact value;
- **GDPR Article 17 erasure** by deleting a data subject's key;
- pluggable key stores, so keys live somewhere other than beside the data they protect.

Its documentation is at [docs.tayra.dev](https://docs.tayra.dev).

::: warning
**Today Tayra's Critter Stack integration covers Marten and Wolverine, not Fisher.**

- Tayra.Marten covers documents and events, inline, live and async projections, and multi-tenancy.
- Tayra.Wolverine covers message bodies, the outbox, the inbox and dead letters.

So a Wolverine application on Fisher gets Tayra's coverage for its messages. Fisher's own documents and
events have no Tayra integration yet. Check [docs.tayra.dev](https://docs.tayra.dev) for the current
list before planning around it.
:::

Fisher does not ship a crypto-shredding mechanism of its own, and will not. Tayra is the answer, and
a second, Fisher-only mechanism would be one that application code could not carry to Marten or
Polecat.

## What Fisher has built in, and why none of it is erasure

Fisher has several operations that remove or hide event data. Each answers a different question, and
none of them reaches every copy of the data.

| Operation | What it does | What it does not reach |
| :--- | :--- | :--- |
| [Archiving](/events/archiving) | Marks the stream no longer live | Anything. The events are all still there. |
| [Stream compacting](/events/rewriting#stream-compacting) | Replaces the history with one `Compacted<T>` event | The snapshot itself, which carries the aggregate's state and so any personal data in it. Also anything an `Archiver` copied out first. |
| [Event data masking](/events/rewriting#event-data-masking) | Rewrites protected fields in stored event bodies | Projections, snapshots and flat tables already built from the old bodies, and the aggregate write cache |
| [`TombstoneStream`](/events/archiving#deleting-a-stream-with-tombstonestream) | Hard-deletes the stream and its events | Projected documents built from those events |

Two more gaps apply to every row in the table:

- **Backups.** A copy of the database file is a copy of the data. Nothing done to the live file
  reaches it.
- **The file itself.** SQLite does not necessarily overwrite a page when a row on it is deleted or
  rewritten. The old content can stay in the file's free pages, and in the write-ahead log, until that
  space is reused. SQLite's `secure_delete` PRAGMA and `VACUUM` reduce this. Neither one reaches a
  backup.

Crypto-shredding avoids both gaps, because it removes the key rather than chasing every copy of the
data.

## If you mask instead

[Event data masking](/events/rewriting#event-data-masking) is still the right tool for a narrower
job: removing information you should never have stored, from the live file, as one atomic batch. If
you use it as part of an erasure process:

- **rebuild every projection that read the masked events**, because masking leaves derived state
  alone;
- **leave the affected aggregate types out of the [aggregate write cache](/events/appending#caching-the-aggregate-between-fetches)**,
  because a node's cached baseline was built from the unmasked bodies;
- **decide what happens to backups** separately, since masking cannot reach them.
