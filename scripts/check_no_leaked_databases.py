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

`--since <epoch seconds>` counts only entries modified at or after that moment (fisher#345). A CI
runner starts with an empty temp directory so it never needs this; a developer's machine does not, and
both leaks fisher#345 was filed over turned out to be files an EARLIER, interrupted run left behind — a
test process killed mid-run never disposes its fixtures — reported against the run after it. Pass the
moment the run started:

    start=$(date +%s); <run the suites>; python3 scripts/check_no_leaked_databases.py --since "$start"

`--temp-dir <dir>` (repeatable) checks those directories instead of the system temp directory
(JasperFx/bobcat#565). The supervised build (build/SupervisedTests.cs) points every worker process at a
temp directory of its own, `.nuke/temp/lanes/<suite>-<tfm>/lane-N`, emptied before each run, so the
leak lands there rather than in the system temp directory -- and which lane it landed in says which
test classes to suspect, since lanes are partitioned by class.
"""

import argparse
import os
import sys
import tempfile

PREFIX = "fisher-"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--since", type=float, default=None,
                        help="only count entries modified at or after this Unix time")
    parser.add_argument("--temp-dir", action="append", default=None,
                        help="check this directory instead of the system temp directory (repeatable)")
    args = parser.parse_args()

    temps = args.temp_dir or [tempfile.gettempdir()]
    temp = ", ".join(temps)

    def is_recent(path: str) -> bool:
        if args.since is None:
            return True
        try:
            return os.stat(path).st_mtime >= args.since
        except OSError:
            # Gone between the listing and the stat: whatever it was, it did not survive the run.
            return False

    leaked = []
    for directory in temps:
        try:
            names = os.listdir(directory)
        except OSError as error:
            print(f"Could not read the temp directory {directory}: {error}", file=sys.stderr)
            continue
        # Named relative to the directory when there is only the one, as before; with several the
        # directory is the useful half (it names the lane).
        leaked += sorted(
            name if len(temps) == 1 else os.path.join(directory, name)
            for name in names
            if name.startswith(PREFIX) and is_recent(os.path.join(directory, name))
        )

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
