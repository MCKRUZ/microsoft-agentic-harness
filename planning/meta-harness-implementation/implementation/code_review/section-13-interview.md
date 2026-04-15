# Code Review Interview: Section 13

## Auto-fixed

### M1: stderr unbounded — could OOM on large error output
**Fix:** Capped stderr at 64KB using the same `ReadWithCapAsync` helper. Both stdout (1MB) and stderr (64KB) now drain concurrently with a cap, so neither the process nor the test host can OOM.
- `stderrTask = ReadWithCapAsync(process.StandardError, 65_536, cts.Token)`
- Destructs result as `var (stderr, _) = await stderrTask` (discard truncated flag for stderr)

## User decisions

### M4: `EnableMcpTraceResources=false` asymmetry
**Question:** `ListAsync` returns `[]` when disabled but `ReadAsync` threw `InvalidOperationException`. Consistent behavior?
**User chose:** Both return empty/not-found.
**Fix:** `ReadAsync` now throws `FileNotFoundException` when disabled (consistent "not found" semantics for callers who may try to Read a URI from a cached List result).

## Let go

- M3 (Windows PATH shadowing via ambient PATH): Acceptable POC risk. Documented in review. Production fix would resolve binary via `where`/`which` before execution.
- L1–L3: Nitpicks, no action.
