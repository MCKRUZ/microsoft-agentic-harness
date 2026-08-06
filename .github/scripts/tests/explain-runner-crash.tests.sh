#!/usr/bin/env bash
#
# Tests for explain-runner-crash.sh, plus the one shell behaviour the CI steps that call it depend
# on: `set -o pipefail` must preserve a failing exit code through `tee`.
#
# That second check is the load-bearing one. `dotnet test ... | tee log` without pipefail reports
# SUCCESS on every test failure — strictly worse than having no gate at all — and nothing about the
# YAML would look wrong. The mutation case below is what proves the pipefail line is doing work
# rather than decorating.
#
# Run: bash .github/scripts/tests/explain-runner-crash.tests.sh

set -uo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
subject="$script_dir/../explain-runner-crash.sh"
fails=0

check() {
    if [ "$2" = "$3" ]; then
        printf 'PASS  %s\n' "$1"
    else
        printf 'FAIL  %s (expected %q, got %q)\n' "$1" "$3" "$2"
        fails=$((fails + 1))
    fi
}

# ── pipefail through tee: what the calling CI steps rely on ──────────────────
( set -o pipefail; (exit 7) | tee /dev/null ) >/dev/null 2>&1
check "failing command through tee, WITH pipefail, propagates" "$?" "7"

# Mutation. Drop pipefail and the same pipeline reports success. If this ever starts matching the
# case above, pipefail has stopped mattering and the check above has stopped proving anything.
#
# `set +o pipefail` explicitly, because a subshell INHERITS the option from this script's own
# prologue — without the reset, this line silently re-tests the case above and passes for the wrong
# reason. It did exactly that on first run.
( set +o pipefail; (exit 7) | tee /dev/null ) >/dev/null 2>&1
check "failing command through tee, WITHOUT pipefail, is swallowed (the trap)" "$?" "0"

# ── the subject ─────────────────────────────────────────────────────────────
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

cat > "$tmp/crash.log" <<'EOF'
  Passed!  - Failed: 0, Passed: 2819, Skipped: 3, Total: 2822 - Infrastructure.AI.Tests.dll
Infrastructure.AI.Tests: Catastrophic failure: System.NullReferenceException
   at Microsoft.VisualStudio.TestPlatform.ObjectModel.Navigation.PortableSymbolReader.Dispose()
   at Xunit.Runner.VisualStudio.VsTestRunner.RunTestsInAssembly()
  Passed!  - Failed: 0, Passed: 410, Skipped: 0, Total: 410 - Presentation.AgentHub.Tests.dll
EOF

cat > "$tmp/real-failure.log" <<'EOF'
  [xUnit.net] SomeTests.DoesTheThing [FAIL]
  Failed!  - Failed: 1, Passed: 2818, Skipped: 3, Total: 2822 - Infrastructure.AI.Tests.dll
EOF

run_subject() {
    GITHUB_STEP_SUMMARY="$tmp/summary.md" bash "$subject" "$1" 2>&1
}

: > "$tmp/summary.md"
out="$(run_subject "$tmp/crash.log")"
check "runner-crash log emits the error annotation" \
    "$(printf '%s' "$out" | grep -c '^::error title=Test runner crashed')" "1"
check "runner-crash log writes the excerpt to the job summary" \
    "$(grep -c 'PortableSymbolReader' "$tmp/summary.md")" "1"

: > "$tmp/summary.md"
out="$(run_subject "$tmp/real-failure.log")"
check "ordinary test failure emits no annotation" \
    "$(printf '%s' "$out" | grep -c '^::error')" "0"
check "ordinary test failure writes nothing to the job summary" \
    "$(wc -c < "$tmp/summary.md" | tr -d ' ')" "0"

# A build that fails before dotnet writes anything must not turn one confusing red into two.
: > "$tmp/summary.md"
run_subject "$tmp/does-not-exist.log" >/dev/null
check "missing log exits clean" "$?" "0"

# And it stays clean on a crash, because the calling step runs only when the build already failed.
run_subject "$tmp/crash.log" >/dev/null
check "crash detected still exits clean" "$?" "0"

echo
if [ "$fails" -eq 0 ]; then
    echo "ALL PASS"
else
    echo "$fails CHECK(S) FAILED"
fi
exit "$fails"
