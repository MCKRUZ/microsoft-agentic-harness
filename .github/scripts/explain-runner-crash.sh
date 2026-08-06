#!/usr/bin/env bash
#
# Explains a `dotnet test` failure that is NOT a test failure.
#
# The test runner can crash tearing down its symbol reader AFTER every assembly has already reported
# Passed. The job then ends with nothing but green per-assembly summaries and `Process completed with
# exit code 1`; the only signal is a `Catastrophic failure` line roughly forty lines above the end,
# sandwiched between ordinary skip messages. Anyone reading the tail of the log — or the check name
# alone — concludes a test broke and spends a debugging cycle hunting a product regression that is
# not there. One occurrence cost exactly that (issue #263).
#
# This does not recover the run. It makes the red legible: a GitHub error annotation on the check,
# and the crash excerpt in the job summary.
#
# Usage: explain-runner-crash.sh <path-to-tee'd-test-log>
# Exit:  always 0 — this is a diagnostic, and it runs when the build has already failed. Failing here
#        would replace a confusing message with a confusing message plus a second red step.

set -uo pipefail

log="${1:?usage: explain-runner-crash.sh <test-log>}"

if [ ! -f "$log" ]; then
    echo "explain-runner-crash: no log at '$log' — nothing to explain."
    exit 0
fi

if ! grep -q "Catastrophic failure" "$log"; then
    echo "explain-runner-crash: no runner crash detected — this is a real test failure."
    exit 0
fi

echo "::error title=Test runner crashed during teardown::The test RUNNER crashed, not a test. Every assembly may still report Passed. See issue #263."

# GITHUB_STEP_SUMMARY is absent when this is run locally; fall back to stdout so the script is
# runnable by hand against a downloaded log, which is the situation it exists to serve.
summary="${GITHUB_STEP_SUMMARY:-/dev/stdout}"

{
    echo "## Test runner crashed — this is probably not a product regression"
    echo
    echo "The runner exited non-zero after the tests had already run, so the per-assembly"
    echo "summaries may all say **Passed**. Background and remedies: issue #263."
    echo
    echo '```'
    grep -B 2 -A 12 "Catastrophic failure" "$log" | head -60
    echo '```'
} >> "$summary"

exit 0
