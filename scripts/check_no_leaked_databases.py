#!/usr/bin/env python3
"""Fail if a test run left SQLite files behind in the system temp directory.

fisher#189. Every test database is a real file under `Path.GetTempPath()` -- `TemporaryDatabase`
names it `fisher-<name>-<guid>.db` and deletes it, plus its `-wal` / `-shm` / `-journal` sidecars, on
dispose. Files surviving the run mean something outlived the test that owned them.

That is not a tidiness concern. The way it happened was an async daemon disposed WITHOUT being
stopped first: `IProjectionDaemon` is `IDisposable` only, so `Dispose()` does not await in-flight
shard work, and a shard's next poll RE-CREATES the database file the fixture just deleted. A daemon
still polling after its test finished is doing unpredictable work while other tests run, which is
exactly the class of hazard #189 is about -- the leaked file is simply the visible symptom of it.

So this guards the symptom because the symptom is cheap to see and the cause is not. Eleven of the
twelve files a full `Fisher.Tests` run used to leave behind came from four classes that disposed a
daemon without stopping it; the twelfth was `db-patch`'s `.drop.sql` companion, which the test that
wrote it never deleted.

Run after the suites, and only on a green run: a failing test may legitimately skip its own cleanup,
so a leak reported over a red suite would be noise pointing at the wrong thing.
"""

import os
import sys
import tempfile

PREFIX = "fisher-"


def main() -> int:
    temp = tempfile.gettempdir()

    try:
        leaked = sorted(name for name in os.listdir(temp) if name.startswith(PREFIX))
    except OSError as error:
        print(f"Could not read the temp directory {temp}: {error}", file=sys.stderr)
        return 0

    if not leaked:
        print(f"No test databases left behind in {temp}.")
        return 0

    print(f"{len(leaked)} file(s) left behind in {temp} by the test run:\n")
    for name in leaked[:40]:
        print(f"  - {name}")
    if len(leaked) > 40:
        print(f"  ... and {len(leaked) - 40} more")

    print(
        "\nSomething outlived the test that owned it. The usual cause is an async daemon disposed\n"
        "without `await daemon.StopAllAsync()` first: Dispose() does not await in-flight shard work,\n"
        "so the next poll re-creates the file the fixture had already deleted. Check any test that\n"
        "builds a daemon, and any test that writes a file of its own beside the one it cleans up."
    )

    # Capped: a developer running this against a machine with an accumulated backlog would otherwise
    # emit thousands of annotations. A CI runner starts with an empty temp directory, so on the run
    # this exists to guard, the cap is never reached.
    for name in leaked[:40]:
        print(f"::error::leaked temp file: {name}")

    return 1


if __name__ == "__main__":
    sys.exit(main())
