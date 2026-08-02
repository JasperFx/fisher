# fisher

SQLite Backed Document and Event Store within the Critter Stack

> **Status: early development.** The event store round-trips — append, fetch, stream state — but
> document storage, projections, and the async daemon are not implemented yet. See
> [CLAUDE.md](CLAUDE.md) for the current state and [ROADMAP.md](ROADMAP.md) for what comes next.

## Working with the code

Requires the .NET 10 SDK. No database server is needed — the tests create throwaway SQLite files.

```bash
dotnet build fisher.slnx
dotnet test fisher.slnx
```

## License

Copyright © Jeremy D. Miller and contributors.

Fisher is provided as-is under the MIT license. See [LICENSE](LICENSE).
