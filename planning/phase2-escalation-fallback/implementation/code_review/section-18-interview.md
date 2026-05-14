# Section 18 Code Review Interview

## Triage Summary

| Finding | Severity | Action | Rationale |
|---------|----------|--------|-----------|
| CRITICAL-1: Missing DI | CRITICAL | Let go | Section-19 handles DI registration |
| HIGH-1: Contract violation | HIGH | Auto-fix | Notifier now catches+logs per interface contract |
| MEDIUM-1: Negative remaining | MEDIUM | Auto-fix | Added Math.Max(0, ...) clamp |
| MEDIUM-2: Arguments type | MEDIUM | User decision | Changed to `string,string` per user preference |
| MEDIUM-3: Writer before try | MEDIUM | Auto-fix | Moved inside try block |
| MEDIUM-4: Missing round-trips | MEDIUM | Auto-fix | Added 2 round-trip deserialization tests |
| MEDIUM-5: AsyncLocal tests | MEDIUM | Let go | AsyncLocal is .NET runtime behavior |
| LOW-1: Two types in file | LOW | Let go | Interface + tiny impl is common pattern |

## User Decision

**MEDIUM-2:** User chose `IReadOnlyDictionary<string, string>?` — matches domain model, avoids boxing, safer serialization.

## Fixes Applied

1. **AgUiEscalationNotifier.cs** — All three methods now wrap `writer.WriteAsync` in try-catch, logging at Warning level. `OperationCanceledException` still propagates. `RemainingSeconds` clamped to `Math.Max(0, ...)`. Arguments passed directly without boxing.
2. **AgUiEvents.cs** — `Arguments` property changed from `IReadOnlyDictionary<string, object>?` to `IReadOnlyDictionary<string, string>?`.
3. **AgUiRunHandler.cs** — `_writerAccessor.Writer = writer` moved inside the try block.
4. **AgUiEscalationNotifierTests.cs** — `WriterThrows_PropagatesException` renamed to `WriterThrows_CatchesAndLogs` and now asserts `NotThrowAsync`. Added `NegativeRemaining_ClampsToZero` test.
5. **AgUiEscalationEventSerializationTests.cs** — Added round-trip deserialization tests for `EscalationRequestedEvent` and `EscalationExpiringEvent`.
