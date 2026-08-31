#!/usr/bin/env python3
"""Fail the build when HANDOFF.md's compliance scoreboard disagrees with a real test run.

HANDOFF.md calls itself "the compliance scoreboard", and the numbers in it are hand-maintained. That
is exactly why they rot: nothing recomputes them, so a bump that adds a suite or a PR that adds a
test leaves them quietly wrong. fisher#107 found the file claiming "2.49.0 ships 32 suites, 275
tests" while its own header two hundred lines above already said 2.51.0 -- two scoreboards in one
file disagreeing with each other, and both understating Fisher in a document the 1.0 README points
at as the public account of what Fisher does not do.

This reads the TRX reports CI already produces and compares every machine-checkable number in
HANDOFF.md against them. It asserts nothing about prose: what it checks is counts, the suite split,
the per-suite tables, and the package versions.

It also covers the two other files that carry these numbers, and they need very different treatment:

  README.md   the package front page, so its counts are the ones a prospective user reads first.
              One claim, entirely current.

  ROADMAP.md  **mostly a changelog, and its historical entries must NOT be checked.** The 0.8.1
              entry still says "All 36 compliance suites, 309 tests" because that is what was true
              at 0.8.1, and the 1.0.1 entry quotes the stale "2.49.0 ships 32 suites, 275 tests"
              verbatim as the thing fisher#107 found. Both are correct as written. A sweep over every
              "N suites, M tests" in that file would fail on them immediately and the obvious "fix"
              would be to rewrite history. Exactly one claim in ROADMAP is about the present tense,
              and it is the only one checked here.

Usage:
    python3 scripts/check_scoreboard.py --tfm net10.0 --configuration Release

Run it after the test step, in the same job, so the TRX files are on disk. A claim this script
cannot find is a failure too -- rewording a sentence that carries a number means teaching this
script the new wording, which is the point rather than an inconvenience.
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# The three test projects, in the order HANDOFF.md's header names them.
ASSEMBLIES = ["Fisher.Tests", "Fisher.AspNetCore.Tests", "Fisher.EntityFrameworkCore.Tests"]

COMPLIANCE_NAMESPACE = "Fisher.Tests.Compliance."
ENROLLMENT = Path("src/Fisher.Tests/Compliance/fisher_event_store_compliance.cs")
DOCUMENT_FIXTURE = "FisherDocumentComplianceFixture"

HANDOFF = Path("HANDOFF.md")
README = Path("README.md")
ROADMAP = Path("ROADMAP.md")
PACKAGES = Path("Directory.Packages.props")

ONES = [
    "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
    "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
    "nineteen",
]
TENS = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"]


def number_word(n: int) -> str:
    """Spell 0-99, so the enrollment file's prose count can be checked as prose."""
    if n < 20:
        return ONES[n]
    tens, ones = divmod(n, 10)
    return TENS[tens] if ones == 0 else f"{TENS[tens]}-{ONES[ones]}"


class Failures:
    def __init__(self) -> None:
        self.items: list[str] = []

    def add(self, message: str) -> None:
        self.items.append(message)

    def expect(self, label: str, claimed: int | str | None, actual: int | str) -> None:
        # README writes its counts for a human ("1,320"), so a bare string compare would report a
        # mismatch that is only formatting. Normalising here rather than at each call site keeps the
        # HANDOFF checks — which are unformatted — reading exactly as before.
        if isinstance(claimed, str):
            claimed = claimed.replace(",", "")

        if claimed is None:
            self.add(
                f"{label}: could not find this claim in the file. If the wording changed, update "
                f"scripts/check_scoreboard.py to match — the real value is {actual}."
            )
        elif str(claimed) != str(actual):
            self.add(f"{label}: says {claimed}, the run says {actual}.")


def read_trx(root: Path, assembly: str, configuration: str, tfm: str) -> ET.ElementTree:
    directory = root / "src" / assembly / "bin" / configuration / tfm / "TestResults"
    reports = sorted(directory.glob("*.trx"))

    if not reports:
        sys.exit(
            f"No TRX report under {directory}. This script runs after the test step in the same "
            f"job; it cannot recompute the numbers without one."
        )

    # Newest, so a stale report from an earlier local run is never the one compared against.
    return ET.parse(max(reports, key=lambda p: p.stat().st_mtime))


def counters(tree: ET.ElementTree) -> tuple[int, int]:
    node = tree.getroot().find("t:ResultSummary/t:Counters", TRX_NS)
    if node is None:
        sys.exit("TRX report has no ResultSummary/Counters element.")
    return int(node.get("total", 0)), int(node.get("passed", 0))


def class_counts(tree: ET.ElementTree) -> dict[str, int]:
    counts: dict[str, int] = {}
    for unit in tree.getroot().findall(".//t:UnitTest", TRX_NS):
        method = unit.find("t:TestMethod", TRX_NS)
        if method is None:
            continue
        name = method.get("className")
        if name:
            counts[name] = counts.get(name, 0) + 1
    return counts


def enrollment_map(root: Path) -> tuple[dict[str, str], set[str]]:
    """Fisher's subclass name -> the upstream suite it closes, plus which are document suites.

    HANDOFF's per-suite tables are keyed by the upstream name (`StreamReadCompliance`), while a TRX
    reports Fisher's subclass (`stream_read_compliance`), so the enrollment file is what joins them.
    Reading it rather than hardcoding a table is also what makes a newly enrolled suite show up here
    without this script being edited.
    """
    source = (root / ENROLLMENT).read_text(encoding="utf-8")

    declarations = re.findall(
        r"public\s+class\s+(\w+)\s*:\s*(\w+)\s*<([^>]*)>", source, re.MULTILINE
    )

    suites: dict[str, str] = {}
    documents: set[str] = set()

    for fisher_class, upstream, arguments in declarations:
        suites[fisher_class] = upstream
        if DOCUMENT_FIXTURE in arguments:
            documents.add(fisher_class)

    if not suites:
        sys.exit(f"Parsed no enrolled suites out of {ENROLLMENT}. The declaration shape changed.")

    return suites, documents


def package_version(text: str, package: str) -> str | None:
    match = re.search(
        rf'<PackageVersion\s+Include="{re.escape(package)}"\s+Version="([^"]+)"', text
    )
    return match.group(1) if match else None


def find(pattern: str, text: str, group: int = 1) -> str | None:
    match = re.search(pattern, text)
    return match.group(group) if match else None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tfm", required=True)
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--root", default=".")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    handoff = (root / HANDOFF).read_text(encoding="utf-8")
    packages = (root / PACKAGES).read_text(encoding="utf-8")

    failures = Failures()

    # ---- what the run actually did -------------------------------------------------------------
    totals: dict[str, int] = {}
    compliance: dict[str, int] = {}

    for assembly in ASSEMBLIES:
        tree = read_trx(root, assembly, args.configuration, args.tfm)
        total, passed = counters(tree)
        totals[assembly] = total

        if total != passed:
            failures.add(
                f"{assembly}: {total - passed} of {total} tests did not pass, so the scoreboard's "
                f"'green' claim cannot be checked against this run."
            )

        if assembly == "Fisher.Tests":
            for name, count in class_counts(tree).items():
                if name.startswith(COMPLIANCE_NAMESPACE):
                    compliance[name[len(COMPLIANCE_NAMESPACE):]] = count

    suites, documents = enrollment_map(root)

    if not compliance:
        sys.exit(
            f"No tests found under {COMPLIANCE_NAMESPACE} in Fisher.Tests' TRX. Either the "
            f"namespace moved or the suites did not run; refusing to report a green scoreboard."
        )

    grand_total = sum(totals.values())
    compliance_total = sum(compliance.values())
    document_total = sum(n for cls, n in compliance.items() if cls in documents)
    event_total = compliance_total - document_total
    document_suites = len([cls for cls in compliance if cls in documents])
    event_suites = len(compliance) - document_suites

    # A suite enrolled but not run at all would otherwise be invisible in every number above.
    missing = sorted(set(suites) - set(compliance))
    if missing:
        failures.add(
            "enrolled but absent from the run: " + ", ".join(missing) +
            " — the scoreboard would report a suite count nothing executed."
        )

    # ---- what HANDOFF.md claims ----------------------------------------------------------------
    header = find(r"\*\*(\d+) tests green on net9\.0 and net10\.0\*\*", handoff)
    failures.expect("HANDOFF header, total tests", header, grand_total)

    per_project = re.search(
        r"(\d+)\s+in\s*\n?`Fisher\.Tests`,\s*(\d+)\s+in\s+`Fisher\.AspNetCore\.Tests`\s+and\s+(\d+)\s+in\s+"
        r"`Fisher\.EntityFrameworkCore\.Tests`",
        handoff,
    )
    if per_project is None:
        failures.expect("HANDOFF header, per-project split", None, str(totals))
    else:
        for assembly, claimed in zip(ASSEMBLIES, per_project.groups()):
            failures.expect(f"HANDOFF header, {assembly}", claimed, totals[assembly])

    failures.expect(
        "HANDOFF header, compliance total",
        find(r"(\d+) of\s*\n?them are shared cross-store compliance tests", handoff),
        compliance_total,
    )
    failures.expect(
        "HANDOFF header, event sourcing tests",
        find(r"compliance tests — (\d+) event sourcing", handoff),
        event_total,
    )
    failures.expect(
        "HANDOFF header, document tests",
        find(r"and (\d+) document\.", handoff),
        document_total,
    )

    # Until JasperFx 2.59.0 every shipped suite was an enrolled suite, so one number served both and
    # the sentence read "ships **N suites, M tests**". 2.59.0 broke that: it added two opt-in suites
    # Fisher does not enroll (jasperfx#724, whose precondition Fisher cannot construct at all -- see
    # jasperfx#727 -- and jasperfx#725, which needs a fixture seam). The claim now has to separate what
    # the library ships from what Fisher enrolls, and only the second is checkable from a test run.
    ships = re.search(
        r"`JasperFx\.Events\.ComplianceTests` ([\d.]+) ships \d+ suites; "
        r"Fisher enrolls \*\*(\d+) of them, (\d+) tests\*\*",
        handoff,
    )
    if ships is None:
        failures.expect("HANDOFF compliance section, 'ships N suites; Fisher enrolls M of them, K tests'", None,
                        f"{len(compliance)} suites, {compliance_total} tests")
    else:
        failures.expect("compliance section, suites", ships.group(2), len(compliance))
        failures.expect("compliance section, tests", ships.group(3), compliance_total)

    passes = re.search(r"Fisher passes \*\*all (\d+), all\s*\n?(\d+) suites\*\*", handoff)
    if passes is None:
        failures.expect("HANDOFF compliance section, 'Fisher passes all N, all M suites'", None,
                        f"{compliance_total}, {len(compliance)}")
    else:
        failures.expect("compliance section, passes tests", passes.group(1), compliance_total)
        failures.expect("compliance section, passes suites", passes.group(2), len(compliance))

    green = re.search(r"### Green — (\d+) suites, (\d+) tests", handoff)
    if green is None:
        failures.expect("HANDOFF '### Green — N suites, M tests'", None,
                        f"{len(compliance)} suites, {compliance_total} tests")
    else:
        failures.expect("Green heading, suites", green.group(1), len(compliance))
        failures.expect("Green heading, tests", green.group(2), compliance_total)

    event_heading = re.search(r"Event sourcing — (\d+) suites, (\d+) tests:", handoff)
    if event_heading is None:
        failures.expect("HANDOFF 'Event sourcing — N suites, M tests'", None,
                        f"{event_suites} suites, {event_total} tests")
    else:
        failures.expect("event table heading, suites", event_heading.group(1), event_suites)
        failures.expect("event table heading, tests", event_heading.group(2), event_total)

    document_heading = re.search(r"Documents — (\d+) suites, (\d+) tests,", handoff)
    if document_heading is None:
        failures.expect("HANDOFF 'Documents — N suites, M tests'", None,
                        f"{document_suites} suites, {document_total} tests")
    else:
        failures.expect("document table heading, suites", document_heading.group(1), document_suites)
        failures.expect("document table heading, tests", document_heading.group(2), document_total)

    # ---- the per-suite tables ------------------------------------------------------------------
    tabled = {
        name: int(count)
        for name, count in re.findall(r"^\|\s*`(\w+Compliance)`\s*\|\s*(\d+)\s*\|$", handoff, re.M)
    }
    actual_by_upstream = {suites[cls]: count for cls, count in compliance.items() if cls in suites}

    for upstream, actual in sorted(actual_by_upstream.items()):
        if upstream not in tabled:
            failures.add(
                f"per-suite table: `{upstream}` ({actual} tests) is enrolled and running but has no "
                f"row. A newly enrolled suite needs one."
            )
        elif tabled[upstream] != actual:
            failures.add(f"per-suite table, `{upstream}`: says {tabled[upstream]}, ran {actual}.")

    for upstream in sorted(set(tabled) - set(actual_by_upstream)):
        failures.add(
            f"per-suite table: `{upstream}` has a row but did not run. Remove the row, or find out "
            f"why the suite is no longer enrolled."
        )

    # ---- versions ------------------------------------------------------------------------------
    for label, package, pattern in [
        ("HANDOFF header, JasperFx version", "JasperFx", r"On JasperFx \*\*([\d.]+)\*\*"),
        ("HANDOFF header, Weasel version", "Weasel.Sqlite", r"/ Weasel \*\*([\d.]+)\*\*"),
    ]:
        pinned = package_version(packages, package)
        if pinned is None:
            failures.add(f"{package} is not pinned in {PACKAGES}.")
        else:
            failures.expect(label, find(pattern, handoff), pinned)

    compliance_pinned = package_version(packages, "JasperFx.Events.ComplianceTests")
    if ships is not None and compliance_pinned is not None:
        failures.expect("compliance section, package version", ships.group(1), compliance_pinned)

    # ---- the enrollment file's own prose count -------------------------------------------------
    # Same wording change as `ships` above, and for the same reason: "All <n> that ship ... are
    # enrolled" stopped being true at JasperFx 2.59.0.
    enrolled_claim = re.search(
        r"([\w-]+) are enrolled from\s*\n?\s*\*?\s*JasperFx\.Events\.ComplianceTests ([\d.]+)",
        (root / ENROLLMENT).read_text(encoding="utf-8"),
    )
    if enrolled_claim is None:
        failures.add(
            f"{ENROLLMENT}: could not find its '<n> are enrolled from JasperFx.Events.ComplianceTests "
            f"<version>' comment. It is the third place these numbers live; update this script if it "
            f"was reworded."
        )
    else:
        failures.expect("enrollment comment, suite count", enrolled_claim.group(1),
                        number_word(len(compliance)))
        if compliance_pinned is not None:
            failures.expect("enrollment comment, package version", enrolled_claim.group(2),
                            compliance_pinned)

    # ---- README.md ------------------------------------------------------------------------------
    # The package front page. Its numbers are the first a prospective user sees, and it was ahead of
    # HANDOFF when fisher#107 was filed -- which is how the disagreement became visible at all.
    readme = (root / README).read_text(encoding="utf-8")

    readme_claim = re.search(r"\*\*all (\d+) suites and (\d+) tests\*\*", readme)
    if readme_claim is None:
        failures.expect("README, 'all N suites and M tests'", None,
                        f"{len(compliance)} suites, {compliance_total} tests")
    else:
        failures.expect("README, suites", readme_claim.group(1), len(compliance))
        failures.expect("README, compliance tests", readme_claim.group(2), compliance_total)

    failures.expect(
        "README, Fisher.Tests count",
        find(r"alongside its own ([\d,]+)", readme),
        totals["Fisher.Tests"],
    )

    # ---- ROADMAP.md -----------------------------------------------------------------------------
    # One claim, deliberately. See the module docstring for why a sweep here would be wrong: the file
    # is a changelog, and its per-release entries are meant to hold the numbers that were true then.
    roadmap = (root / ROADMAP).read_text(encoding="utf-8")

    failures.expect(
        "ROADMAP, 'green on all <n>'",
        find(r"Being green on all ([\w-]+) is not the same as being feature-complete", roadmap),
        number_word(len(compliance)),
    )

    # ---- report --------------------------------------------------------------------------------
    if failures.items:
        print(f"The scoreboard disagrees with the {args.tfm} run:\n", file=sys.stderr)
        for item in failures.items:
            print(f"  - {item}", file=sys.stderr)
            print(f"::error::scoreboard: {item}")
        print(
            "\nHANDOFF.md is the compliance scoreboard and README.md is the package front page, so "
            "these numbers are load-bearing rather than decorative. Update them from a real run — "
            "never by arithmetic.\n\nIf a ROADMAP line is reported here, check it is the present-tense "
            "one before editing: that file's per-release entries are history and are supposed to keep "
            "the numbers that were true at the time.",
            file=sys.stderr,
        )
        return 1

    print(
        f"Scoreboard agrees with the {args.tfm} run: {grand_total} tests, "
        f"{compliance_total} compliance across {len(compliance)} suites "
        f"({event_suites} event / {document_suites} document)."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
