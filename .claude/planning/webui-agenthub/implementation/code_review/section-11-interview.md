# Section 11 — Code Review Interview Transcript

**Date:** 2026-04-16
**Section:** section-11-telemetry-mcp-panel

---

## Decisions

### H1 — Tool name URL injection (AUTO-FIX)
Wrap `name` with `encodeURIComponent` in `useMcpQuery.ts:47`.
No user input needed — clear security fix.

### H2 — RightPanel uses selectedAgent instead of conversationId (AUTO-FIX)
Replace `useAppStore((s) => s.selectedAgent)` with `useChatStore((s) => s.conversationId)` in `RightPanel.tsx`.
Semantic bug: agent name ≠ conversation UUID. Always showed zero spans.

### M1 — Null conversationId passed to SignalR (AUTO-FIX)
Guard `invokeToolViaAgent` in `ToolInvoker.tsx` — show error if `conversationId` is null rather than sending empty string.

### M2 — Via Agent mode has no pending/disabled state (AUTO-FIX)
Add `isSubmitting` state for the async Via Agent path in `ToolInvoker.tsx`. Include in disabled condition.

### M3 — Non-null assertion in buildSpanTree (AUTO-FIX)
Replace `has() + get()!` with single `get()` and truthiness guard in `buildSpanTree.ts`.

### M4 — API response validation (USER: Full Zod schemas)
User chose full Zod schemas. Add `z.object(...)` schemas for `McpTool`, `McpResource`, `McpPrompt`, and `Agent`. Use `.parse()` in all query `queryFn` implementations.

### M5 — Unbounded conversationSpans growth (AUTO-FIX)
Add `MAX_CONVERSATION_SPANS = 200` and apply same slice-to-cap pattern as `addGlobalSpan` in `telemetryStore.ts`.

### M6 — SpanNode recursive depth (AUTO-FIX)
Add `MAX_DEPTH = 20` guard and early return in `SpanNode.tsx`. Prevents stack overflow from deep/cyclic traces.

---

## Let Go

- L1 (shared QueryStateGuard): premature abstraction at 3 instances
- L2 (TracesPanel clear button duplication): minor, spec-correct as-is
- L3 (test file location): spec explicitly places MCP tests under telemetry/__tests__
- L4 (eslint-disable comments): suppressions are correct; explanatory comments noted
- I1 (void + .catch swallowing errors): known POC limitation
