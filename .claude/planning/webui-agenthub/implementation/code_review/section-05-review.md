# Section 05 -- OTel Bridge Code Review

**Reviewer**: claude-code-reviewer
**Date**: 2026-04-15
**Verdict**: WARNING -- No CRITICAL issues. Two HIGH issues that should be fixed before merge. Several MEDIUM items worth addressing.

---

## HIGH Issues

### [HIGH-01] Magic strings duplicated between SignalRSpanExporter and AgentTelemetryHub
**File**: Presentation.AgentHub/Telemetry/SignalRSpanExporter.cs:160-166

SignalRSpanExporter.DrainAsync uses hardcoded string literals for event names and group names, but AgentTelemetryHub already defines these as constants (EventSpanReceived, ConversationGroup, GlobalTracesGroup). If either side renames a group or event, the other silently stops working with no compile-time error.

**Fix**: Make ConversationGroup and GlobalTracesGroup internal on the hub. Reference AgentTelemetryHub.ConversationGroup(), AgentTelemetryHub.GlobalTracesGroup, and AgentTelemetryHub.EventSpanReceived from the exporter.

---

### [HIGH-02] DrainAsync has no exception handling -- a single SignalR failure kills the drain loop
**File**: Presentation.AgentHub/Telemetry/SignalRSpanExporter.cs:151-169

DrainAsync has no try/catch around await Task.WhenAll(tasks). If SendAsync throws (disconnected client, hub disposal, serialization error), the exception propagates out of the await foreach, terminating the drain loop permanently. Every subsequent span is silently lost.

**Fix**: Wrap per-span sends in try/catch (Exception ex) when (ex is not OperationCanceledException), log the error, and continue the loop.

---

## MEDIUM Issues

### [MEDIUM-01] wasFull race condition produces misleading log messages
**File**: Presentation.AgentHub/Telemetry/SignalRSpanExporter.cs:70-71

Reader.Count is a snapshot. The log says the current span was dropped, but DropOldest evicts the oldest item, not the current write. Purely diagnostic -- not a correctness issue.

**Fix**: Rephrase to: OTel channel at capacity -- oldest span was evicted. Current write: {SpanName}

### [MEDIUM-02] _drainTask field lacks volatile/memory barrier
**File**: Presentation.AgentHub/Telemetry/SignalRSpanExporter.cs:41

StartAsync writes _drainTask on one thread, StopAsync reads it on another. No memory barrier guarantees visibility. Low practical risk due to .NET hosting synchronization.

**Fix** (optional): Use Volatile.Write in StartAsync.

### [MEDIUM-03] StopAsync ignores its cancellationToken parameter
**File**: Presentation.AgentHub/Telemetry/SignalRSpanExporter.cs:145-149

await _drainTask waits indefinitely if the drain loop is processing a backlog. The host shutdown timeout applies, but the exporter should cooperate with it.

**Fix**: await _drainTask.WaitAsync(cancellationToken)

### [MEDIUM-04] List<Task> allocated per span in drain loop hot path
**File**: Presentation.AgentHub/Telemetry/SignalRSpanExporter.cs:155

Always 1 or 2 tasks. Use Task.WhenAll(task1, task2) overload directly instead of building a list.

---

## LOW Issues

### [LOW-01] ActivityKind.Producer and Consumer collapsed to internal
**File**: Presentation.AgentHub/Telemetry/SignalRSpanExporter.cs:102-107

Consider mapping all 5 OTel kinds to avoid confusing WebUI users on messaging spans.

### [LOW-02] Tests use Task.Delay(150) for drain loop sync
**File**: Presentation.AgentHub.Tests/Telemetry/SignalRSpanExporterTests.cs:194, 219

Timing-based assertions flake on slow CI. Consider a TaskCompletionSource signal instead.

### [LOW-03] Activity not disposed in MapToSpanData_ChildSpan test
**File**: Presentation.AgentHub.Tests/Telemetry/SignalRSpanExporterTests.cs:136-151

Activity is started/stopped but not disposed. Other tests use using var.

---

## INFO

### [INFO-01] Tags forwarded verbatim may include sensitive values
All string-valued activity tags are broadcast to SignalR clients. Consider a tag allowlist before GA.

### [INFO-02] Good architectural decisions
- Bounded Channel + DropOldest is correct for telemetry backpressure
- file sealed class for AgentHubSpanExportProcessor keeps it invisible
- InternalsVisibleTo for MapToSpanData is the right testing seam
- IHostedService dual registration via GetRequiredService forwarding is correct

---

## Constraint Verification

| Constraint | Status |
|-----------|--------|
| Export() never blocks, always returns Success | PASS |
| ParentSpanId null for root spans (not default string) | PASS |
| ConversationId from agent.conversation_id tag | PASS |
| Drain loop uses await Task.WhenAll, no Task.Run | PASS |
| Channel capacity exactly 1000, DropOldest | PASS |
| No sensitive data leaked, no injection vectors | WARN -- INFO-01 |
| wasFull thread safety | WARN -- MEDIUM-01 |

---

## Summary

| Severity | Count | Verdict |
|----------|-------|---------|
| CRITICAL | 0 | -- |
| HIGH | 2 | Must fix before merge |
| MEDIUM | 4 | Should fix |
| LOW | 3 | Consider |
| INFO | 2 | -- |

**Blocking**: HIGH-01 (magic strings) and HIGH-02 (unhandled drain loop exceptions). Both are straightforward fixes.
