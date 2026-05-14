# Section 09 Code Review: Drift Baseline Store

## Verdict: WARNING — MEDIUM issues only, can merge with fixes

### MEDIUM
1. **Missing input validation in BuildId** — No null/whitespace/colon guard unlike GraphEwmaStateStore.BuildId
2. **GetAllNodesAsync performance** — Full graph scan, design-level issue
3. **Case-sensitivity divergence** — InMemory uses raw keys, Graph normalizes to lowercase
4. **Non-atomic writes** — Three sequential awaits without cleanup on partial failure

### LOW
5. Null-forgiving operator on JsonSerializer.Deserialize
6. Missing test for GetBaselinesAsync when store throws
7. Missing test for corrupted node properties
