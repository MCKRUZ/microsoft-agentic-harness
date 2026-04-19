# Code Review Interview — section-05-otel-bridge

## Review Summary
- CRITICAL: 0
- HIGH: 2 (both fixed)
- MEDIUM: 4 (auto-handled or let go)
- LOW/INFO: 5

---

## HIGH-01: Magic strings in DrainAsync

**Finding:** `DrainAsync` hardcoded `"SpanReceived"`, `"conversation:{id}"`, and `"global-traces"` as string literals.
`AgentTelemetryHub` already defined `EventSpanReceived` (public const), `ConversationGroup()` (private static),
and `GlobalTracesGroup` (private const). Silent divergence risk if either side changes.

**User decision:** Make `ConversationGroup` and `GlobalTracesGroup` internal; reference from exporter.

**Fix applied:**
- `AgentTelemetryHub.cs`: `private` → `internal` for `ConversationGroup` and `GlobalTracesGroup`
- `SignalRSpanExporter.DrainAsync`: replaced string literals with `AgentTelemetryHub.ConversationGroup(...)`,
  `AgentTelemetryHub.GlobalTracesGroup`, `AgentTelemetryHub.EventSpanReceived`

---

## HIGH-02: Unhandled exceptions in drain loop

**Finding:** `DrainAsync` had no try/catch. A single `SendAsync` failure (disconnected client,
serialization error) would terminate the `await foreach` permanently, silently dropping all
subsequent spans for the process lifetime.

**Auto-fix applied:** Wrapped the per-span send block in
`catch (Exception ex) when (ex is not OperationCanceledException)` with `LogWarning`.
`OperationCanceledException` is re-thrown to allow clean shutdown via `StopAsync`.

---

## MEDIUM findings — let go (POC acceptable)

- **MEDIUM-01:** `wasFull` check is non-atomic (TOCTOU). Acknowledged — the check is a hint for
  logging, not a guarantee. Dropping a span silently vs logging one extra warning has no correctness
  impact.
- **MEDIUM-02:** `MapToSpanData` allocates a `Dictionary` per span. Acceptable for a POC; pool or
  `FrozenDictionary` is a future optimization.
- **MEDIUM-03:** `DrainAsync` allocates `new List<Task>()` per span. Acceptable; stackalloc or
  reuse is a future optimization.
- **MEDIUM-04:** Tags cast via `as IReadOnlyDictionary<string, string>` with null-coalescing fallback
  is redundant (`ToDictionary` never returns null). Harmless.

---

## Test verification post-fix

`dotnet test --filter "FullyQualifiedName~Presentation.AgentHub.Tests"` — 44/44 passed.
