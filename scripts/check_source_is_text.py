#!/usr/bin/env python3
"""Fail the build when a tracked source file contains a NUL byte.

fisher#223. `src/Fisher/Storage/DocumentMapping.cs` carried a literal U+0000 -- the grouping key
for an unnamed [Index] attribute, written as a real NUL rather than the `\0` escape sequence. The
intent was sound (an unnamed index needs a key that cannot collide with a caller-supplied
IndexName), but git's binary heuristic fires on the first NUL in the first 8000 bytes, so for that
file:

  * `git diff` reported "Binary files differ" instead of showing the change,
  * `git merge` could not merge it -- a three-way merge had to be done by hand, replacing the NUL
    with a sentinel, running `git merge-file`, and putting it back. A binary conflict also leaves
    OUR side in the working tree with no conflict markers, which is easy to mistake for "already
    resolved" and quietly drop the other side's work.
  * `grep` skipped it without `-a`, so repository-wide searches silently returned nothing. Several
    searches did exactly that before anyone noticed.

That file is one of the most-edited in the repository, so the cost recurred on every change to it.
The fix was one character; this script is what stops it coming back, in the same spirit as
check_scoreboard.py -- a property nothing else in the build would notice going away.

Checks every git-tracked file with a source-ish extension. Deliberately driven off `git ls-files`
rather than a directory walk so build output, restored packages and local scratch files cannot
fail the build.
"""

import subprocess
import sys
from pathlib import Path

# Text formats where a NUL is always a mistake. Binary assets (png, ico, nupkg, ...) are simply not
# listed -- the point is the diff/merge/grep tax on files humans edit, not a blanket ban.
SUFFIXES = {
    ".cs", ".fs", ".vb", ".csproj", ".fsproj", ".props", ".targets", ".slnx", ".sln",
    ".json", ".yml", ".yaml", ".xml", ".config", ".md", ".sql", ".sh", ".ps1", ".py",
    ".txt", ".editorconfig",
}


def tracked_files(root: Path) -> list[Path]:
    out = subprocess.run(
        ["git", "ls-files", "-z"],
        cwd=root, check=True, capture_output=True, text=True,
    ).stdout
    return [root / name for name in out.split("\0") if name]


def main() -> int:
    root = Path(__file__).resolve().parent.parent

    offenders = []
    for path in tracked_files(root):
        if path.suffix.lower() not in SUFFIXES:
            continue
        try:
            data = path.read_bytes()
        except FileNotFoundError:
            # Tracked but absent: a deletion staged in the working tree. Not this script's business.
            continue

        offset = data.find(b"\0")
        if offset != -1:
            offenders.append((path.relative_to(root), offset, data.count(b"\0")))

    if not offenders:
        return 0

    print("NUL bytes found in tracked source files (fisher#223).", file=sys.stderr)
    print(file=sys.stderr)
    for rel, offset, count in offenders:
        print(f"  {rel}: {count} NUL byte(s), first at offset {offset}", file=sys.stderr)
    print(file=sys.stderr)
    print(
        "git treats such a file as binary: no diffs, no automatic merge, and grep skips it.\n"
        'If you need a NUL in a string, write the escape sequence -- $"\\0{x}" -- which compiles\n'
        "to the same value and leaves the source plain text.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
