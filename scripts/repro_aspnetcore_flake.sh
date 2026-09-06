#!/usr/bin/env bash
# fisher#189. Reproduce the Fisher.AspNetCore.Tests failure that took 12 of 36 on one run and went
# green on every retry, then capture the evidence that run did not.
#
# The issue's own conclusion was that the isolated re-runs proved the wrong thing: six consecutive
# greens on a quiet machine do not test the reported conditions, which were THREE other agents
# running full Fisher.Tests suites concurrently. This script reproduces that -- background load
# first, then the target project in a loop -- and on the first failure prints the failing test
# names out of the TRX, which is the single piece of evidence #189 says was missing.
#
#   ./scripts/repro_aspnetcore_flake.sh [iterations] [load-runners] [tfm]
#
# Defaults: 20 iterations, 3 concurrent load runners (the reported number), net9.0.
# Exit 0 = never reproduced (and says so -- that is a real result, not a pass).
# Exit 1 = reproduced; the TRX path and the failing test names are printed.

set -uo pipefail

ITERATIONS="${1:-20}"
LOAD_RUNNERS="${2:-3}"
TFM="${3:-net9.0}"

cd "$(dirname "$0")/.."
ROOT="$(pwd)"

TARGET="src/Fisher.AspNetCore.Tests/bin/Release/$TFM/Fisher.AspNetCore.Tests"
LOAD="src/Fisher.Tests/bin/Release/$TFM/Fisher.Tests"
OUT="$ROOT/artifacts/flake-189"

echo "==> Building Release/$TFM"
dotnet build src/Fisher.Tests/Fisher.Tests.csproj -c Release -f "$TFM" --nologo -v q || exit 2
dotnet build src/Fisher.AspNetCore.Tests/Fisher.AspNetCore.Tests.csproj -c Release -f "$TFM" --nologo -v q || exit 2

[ -x "$TARGET" ] || { echo "no target executable at $TARGET" >&2; exit 2; }
[ -x "$LOAD" ]   || { echo "no load executable at $LOAD" >&2; exit 2; }

mkdir -p "$OUT"

LOAD_PIDS=()
cleanup() {
    for pid in "${LOAD_PIDS[@]:-}"; do
        kill "$pid" 2>/dev/null
    done
    wait 2>/dev/null
}
trap cleanup EXIT INT TERM

echo "==> Starting $LOAD_RUNNERS concurrent full Fisher.Tests runs as background load"
for i in $(seq 1 "$LOAD_RUNNERS"); do
    ( while :; do "$LOAD" >"$OUT/load-$i.log" 2>&1; done ) &
    LOAD_PIDS+=($!)
done

# Let the load runners get past startup and into real work before measuring anything -- the report
# was of the FIRST invocation after a build, with the other suites already in flight.
sleep 20

echo "==> Looping Fisher.AspNetCore.Tests up to $ITERATIONS times under load"
for i in $(seq 1 "$ITERATIONS"); do
    trx="aspnetcore-189-$i.trx"
    "$TARGET" --report-trx --report-trx-filename "$trx" >"$OUT/run-$i.log" 2>&1
    code=$?

    if [ $code -eq 0 ]; then
        printf '  run %2d/%s  green\n' "$i" "$ITERATIONS"
        continue
    fi

    trx_path="$ROOT/src/Fisher.AspNetCore.Tests/bin/Release/$TFM/TestResults/$trx"
    echo
    echo "!!! REPRODUCED on run $i (exit $code)"
    echo "    console: $OUT/run-$i.log"
    echo "    TRX:     $trx_path"
    echo

    if [ -f "$trx_path" ]; then
        cp "$trx_path" "$OUT/"
        echo "    Failing tests:"
        # UnitTestResult carries outcome + testName; keep it to grep/sed so the script needs
        # nothing installed beyond what a dotnet box already has.
        grep -o 'testName="[^"]*"[^>]*outcome="Failed"' "$trx_path" \
            | sed 's/testName="//; s/"[^>]*outcome="Failed"//' \
            | sed 's/^/      /' \
            | sort -u
        echo
        echo "    Attach $OUT/$trx to fisher#189."
    else
        echo "    No TRX written -- the run died before reporting. Console log is the evidence."
    fi

    exit 1
done

echo
echo "==> Not reproduced in $ITERATIONS runs under $LOAD_RUNNERS-way load."
echo "    That is a result worth recording on fisher#189, not a pass: it narrows the cause"
echo "    away from steady-state contention on this host and toward something about the"
echo "    original run that this does not recreate (cold caches, a different load mix, or"
echo "    the specific concurrent worktrees in flight that day)."
exit 0
