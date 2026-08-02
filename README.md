# fisher

SQLite Backed Document and Event Store within the Critter Stack

> **Status: early development.** The event store append path works end to end; document storage,
> projections, and the async daemon are not implemented yet. See [CLAUDE.md](CLAUDE.md) for the
> current state.

## Working with the code

Requires the .NET 10 SDK. No database server is needed — the tests create throwaway SQLite files.

```bash
dotnet build fisher.slnx
dotnet test fisher.slnx
```

## License

Copyright © Jeremy D. Miller and contributors.

Fisher is provided as-is under the MIT license. See [LICENSE](LICENSE).
