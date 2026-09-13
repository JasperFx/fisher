#!/usr/bin/env python3
"""Fail release prep when ROADMAP's `Status:` line disagrees with the real open-issue set.

ROADMAP.md opens with a `Status:` line that says how many issues are open and which. It is the first
thing anybody reads, and it is the one present-tense claim in that file which `check_scoreboard.py`
deliberately does not check -- so nothing recomputes it, and it rots. fisher#265 was filed after it
was found stale at TWO CONSECUTIVE RELEASES on the same day: once naming two issues as pending work
that had both shipped, and once claiming two open issues when a third had been filed in between.

Both misses have the same cause and neither is anybody's carelessness: an issue's state changed
between releases, and the line is prose.

WHY THIS IS NOT PART OF check_scoreboard.py
-------------------------------------------
That script reads files on disk -- TRX reports, HANDOFF, README, ROADMAP, Directory.Packages.props --
and runs on every push. This one has to ask GitHub, and that is a different kind of check:

  * It needs the network and a token. A rate limit or a blip would turn a green build red for a
    reason unrelated to the code.
  * It couples an unrelated PR to tracker state. Both of the misses above were "somebody filed an
    issue between releases" -- under a per-push check, that filing turns the NEXT CONTRIBUTOR's build
    red for something they did not touch and cannot reasonably fix. That is a worse failure than the
    stale line it prevents.
  * The cadence is wrong. `Status:` is release-prep prose; asking on every push asks far more often
    than the answer can meaningfully change, which is how a check becomes something people route
    around.

So it runs at RELEASE PREP, where the failure lands on the person cutting the release -- who is the
person who can fix it. See .github/workflows/release-prep.yml, which triggers on a pull request
touching Directory.Build.props, and run it by hand any time:

    python3 scripts/check_roadmap_status.py

WHAT IT ASSERTS, AND WHY IT NEEDS A MARKED REGION
-------------------------------------------------
Not "every issue number in the Status block is open" -- the block legitimately discusses CLOSED
issues, because saying what a release finished is half of what a status is for. The current one names
five closed issues while claiming three open ones, and a naive sweep would reject it.

So the claim about what is OPEN is delimited explicitly, and only that region is checked:

    <!-- open-issues -->
    ...the paragraph enumerating what is open...
    <!-- /open-issues -->

Two assertions, and the second is the one that matters:

  1. The count word in the `Status:` line matches how many issues are open.
  2. The issue numbers inside the marked region are EXACTLY the open set.

A count alone would go stale silently the moment a different issue is open -- the same argument
check_scoreboard.py already makes about HANDOFF's red list: a scoreboard that says "four are red"
without saying WHICH four is a number nobody can act on. So the difference is reported both ways.

A missing marker is a failure too, not a skip. Rewording the section means moving the markers with
it, which is the point rather than an inconvenience.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

ROADMAP = Path("ROADMAP.md")
REPO = "JasperFx/fisher"

OPEN_START = "<!-- open-issues -->"
OPEN_END = "<!-- /open-issues -->"

ONES = [
    "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
    "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
    "nineteen",
]
TENS = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"]


def number_word(n: int) -> str:
    if n < 20:
        return ONES[n]
    tens, ones = divmod(n, 10)
    return TENS[tens] if ones == 0 else f"{TENS[tens]}-{ONES[ones]}"


def open_issues() -> set[int]:
    """Issue numbers currently open on the repository.

    `--limit` is deliberately generous and `--json number` deliberately narrow: this asks one
    question and should not depend on anything else the API returns. Pull requests are excluded by
    `gh issue list` already.
    """
    try:
        result = subprocess.run(
            ["gh", "issue", "list", "--repo", REPO, "--state", "open", "--limit", "500",
             "--json", "number"],
            capture_output=True, text=True, check=True,
        )
    except FileNotFoundError:
        sys.exit("The `gh` CLI is not on PATH, so the open-issue set cannot be read.")
    except subprocess.CalledProcessError as e:
        sys.exit(f"`gh issue list` failed:\n{e.stderr.strip()}")

    return {int(x["number"]) for x in json.loads(result.stdout)}


def marked_region(text: str) -> str:
    if OPEN_START not in text or OPEN_END not in text:
        sys.exit(
            f"{ROADMAP} has no {OPEN_START} / {OPEN_END} region, so there is nothing to check the "
            f"open-issue claim against. Wrap the paragraph that enumerates what is open -- and only "
            f"that paragraph, since the rest of the Status block legitimately discusses closed "
            f"issues. See this script's docstring."
        )

    start = text.index(OPEN_START) + len(OPEN_START)
    end = text.index(OPEN_END)

    if end < start:
        sys.exit(f"{ROADMAP}'s {OPEN_END} appears before {OPEN_START}.")

    return text[start:end]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    args = parser.parse_args()

    text = (Path(args.root).resolve() / ROADMAP).read_text(encoding="utf-8")

    actual = open_issues()
    claimed = {int(n) for n in re.findall(rf"{re.escape(REPO)}/issues/(\d+)", marked_region(text))}

    failures: list[str] = []

    for number in sorted(claimed - actual):
        failures.append(
            f"ROADMAP's open-issues region names #{number}, which is not open. If it closed, say so "
            f"outside the region -- what a release finished belongs in the Status block, just not in "
            f"the part that claims what is outstanding."
        )

    for number in sorted(actual - claimed):
        failures.append(
            f"#{number} is open and ROADMAP's open-issues region does not mention it. A count alone "
            f"is a number nobody can act on; say which."
        )

    # The count word in the Status line itself. Checked separately because it is the sentence a
    # reader actually takes away, and it can be wrong while the enumeration below it is right.
    match = re.search(r"^Status: \*\*([\w-]+) open issues?\b", text, re.M)

    if match is None:
        failures.append(
            "Could not find ROADMAP's 'Status: **<n> open issues**' sentence. If the wording changed, "
            "teach this script the new one -- a claim it cannot find is a failure, not a pass."
        )
    elif match.group(1) != number_word(len(actual)):
        failures.append(
            f"ROADMAP's Status line says '{match.group(1)} open issues'; there are "
            f"{number_word(len(actual))} ({len(actual)})."
        )

    if failures:
        print("ROADMAP's Status line disagrees with the open issues:\n", file=sys.stderr)
        for item in failures:
            print(f"  - {item}", file=sys.stderr)
            print(f"::error::roadmap-status: {item}")
        print(
            "\nThis runs at release prep rather than on every push, so the failure is the release's "
            "to fix. ROADMAP's Status line is the first thing a reader sees and nothing else "
            "recomputes it -- see fisher#265.",
            file=sys.stderr,
        )
        return 1

    print(f"ROADMAP's Status line agrees with the {len(actual)} open issues: "
          f"{', '.join('#' + str(n) for n in sorted(actual))}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
