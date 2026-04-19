# Section 11 - Telemetry and MCP Panel: Code Review

**Reviewer:** claude-code-reviewer
**Date:** 2026-04-16
**Scope:** Telemetry store full implementation, span tree builder, telemetry UI (SpanNode, SpanTree, SpanDetail, TracesPanel), RightPanel 5-tab layout, MCP query hooks, MCP UI (ToolInvoker, ToolsBrowser, ResourcesList, PromptsList), agent query hook, Header/AppShell/ChatPanel wiring, 6 test files

---

## Summary

Solid section with clean separation of concerns. The telemetry store uses proper Zustand immutability patterns with spread operators. buildSpanTree is a correct O(n) two-pass algorithm. The MCP components follow a consistent loading/error/empty pattern. Test coverage is good at 52 tests across 6 files.

**Verdict: WARNING -- no CRITICAL issues. Two HIGH issues that should be addressed before merge. Several MEDIUM items worth fixing.**

---

## CRITICAL

None.

---

## HIGH

### H1. Tool name injection in API URL -- path traversal risk in useInvokeTool

**File:** src/features/mcp/useMcpQuery.ts:47

The tool name parameter is interpolated directly into the URL path via template literal in the useInvokeTool mutation. If name contains path-separator characters (e.g., ../../../admin/delete), this constructs an unintended URL. While the backend should validate routes, the frontend should not blindly trust data in URL construction.

The tool name comes from the API response (ToolInvoker receives it from ToolsBrowser which gets it from the tools list query). So the practical attack surface is limited to a compromised backend returning crafted tool names. However, per project security rules: validate at system boundaries.

**Fix:** Encode the tool name with encodeURIComponent before interpolation into the URL path.

---

### H2. RightPanel uses selectedAgent as conversationId for My Traces -- semantic mismatch

**File:** src/features/telemetry/RightPanel.tsx:20

    const activeConversationId = useAppStore((s) => s.selectedAgent);

This reads the selected **agent name** from useAppStore but uses it as a conversationId key to look up conversationSpans. The telemetry store indexes conversation spans by conversation ID (a UUID set in ChatPanel), not by agent name. These are completely different values.

Result: My Traces tab will always show zero spans because the key used for lookup (selectedAgent, e.g. my-agent) will never match the key used for storage (conversationId, a UUID).

**Fix:** Read conversationId from useChatStore instead of selectedAgent from useAppStore.

---

## MEDIUM

### M1. ToolInvoker sends empty string as conversationId when none exists

**File:** src/features/mcp/ToolInvoker.tsx:45

If conversationId is null, an empty string is sent to the SignalR hub via the null-coalescing fallback. The backend may reject this, silently drop it, or create a ghost conversation.

**Fix:** Guard the invocation -- if no conversationId, set an error message and return early instead of sending empty string.

---

### M2. ToolInvoker disabled state only checks direct-mode mutation.isPending, not via-agent loading

**File:** src/features/mcp/ToolInvoker.tsx:91

When in Via Agent mode, the Submit button is not disabled during the async invokeToolViaAgent call. The user can spam-click and trigger multiple concurrent invocations.

**Fix:** Add an isSubmitting state for the via-agent path and include it in the disabled condition.

---

### M3. Non-null assertion in buildSpanTree

**File:** src/features/telemetry/buildSpanTree.ts:14

The ! non-null assertion is safe because line 13 checks nodeMap.has(). However, per project coding conventions, non-null assertions are a code smell. The has + get pattern is a classic Map API footgun where a refactor could separate them.

**Fix:** Use a single get() call with a truthiness guard instead of has() + get()!. This is both safer and slightly more efficient.

---

### M4. Unsafe as cast in query hooks -- API response shape not validated

**Files:**
- src/features/mcp/useMcpQuery.ts:28, 35, 42 (r.data as McpTool/Resource/Prompt[])
- src/features/agents/useAgentsQuery.ts:11 (r.data as Agent[])

All four query hooks cast r.data with as T[] without runtime validation. If the API returns an unexpected shape, the UI will render undefined values silently. This is a system boundary that should have validation.

**Fix (minimum):** Assert the response is an array before casting. Ideally use Zod schemas for full response validation.

---

### M5. addConversationSpan has no cap -- unbounded memory growth

**File:** src/stores/telemetryStore.ts:17-22

addGlobalSpan correctly caps at MAX_GLOBAL_SPANS (500), but addConversationSpan has no limit. A long-running conversation with heavy tracing could accumulate thousands of spans per conversation key without bound.

**Fix:** Apply the same cap pattern with a MAX_CONVERSATION_SPANS constant.

---

### M6. SpanNode recursive rendering has no depth limit

**File:** src/features/telemetry/SpanNode.tsx:36-43

SpanNode renders children recursively with no depth guard. A malformed trace with a deep chain or a cycle (not caught by buildSpanTree) will blow the React call stack. buildSpanTree does not check for cycles -- if span A has parentSpanId=B and span B has parentSpanId=A, both get attached as children of each other, causing infinite recursion.

**Fix:** Add MAX_DEPTH = 20 guard in SpanNode. Consider cycle detection in buildSpanTree.

---

## LOW

### L1. Duplicated loading/error/empty pattern across MCP list components

**Files:** ResourcesList.tsx, PromptsList.tsx, ToolsBrowser.tsx

All three components repeat the same if-isLoading / if-isError / if-empty guard pattern. Acceptable at 3 instances but will become maintenance debt if more MCP entity types are added.

**Suggestion:** Consider a shared QueryStateGuard wrapper. Not urgent.

---

### L2. TracesPanel duplicates clear button in both empty and non-empty states

**File:** src/features/telemetry/TracesPanel.tsx:16-22 and 32-38

The clear button JSX block is copy-pasted between the two branches.

**Fix:** Extract the clear button to a local variable rendered once above the conditional content.

---

### L3. Test file location inconsistency for MCP components

**File:** src/features/telemetry/__tests__/McpLists.test.tsx, ToolsBrowser.test.tsx

MCP component tests live under features/telemetry/__tests__/ but the components they test live under features/mcp/.

**Suggestion:** Move MCP-related tests to features/mcp/__tests__/ to match the source structure.

---

### L4. eslint-disable comments in ChatPanel without explanation

**File:** src/features/chat/ChatPanel.tsx:60, 76

Two eslint-disable comments suppress the exhaustive-deps rule. Both suppressions are correct (stable Zustand selectors and SignalR callbacks), but adding a brief inline comment explaining why would help future reviewers.

---

## INFO

### I1. void + .catch(() => {}) pattern in ChatPanel

**File:** src/features/chat/ChatPanel.tsx:74

The void operator satisfies the no-floating-promises rule, but .catch(() => {}) silently swallows all errors. If the conversation start fails on agent change, the user gets no feedback. Worth noting as a systemic pattern to address.

---

### I2. Good immutability discipline in telemetryStore

The Zustand store uses spread operators throughout. No mutations detected. The clearConversation implementation using Object.fromEntries + filter is correct and avoids mutating the existing record.

---

### I3. buildSpanTree is a clean O(n) algorithm

Two-pass approach (build map, then link parents) is the right choice. No unnecessary sorting. The useMemo in TracesPanel correctly memoizes the tree build with [spans] as the dependency. Performance is appropriate for the expected data volume.

---

## Findings Summary

| Severity | Count |
|----------|-------|
| CRITICAL | 0     |
| HIGH     | 2     |
| MEDIUM   | 6     |
| LOW      | 4     |
| INFO     | 3     |
| **Total**| **15**|

---

## Verdict

**WARNING** -- Approve with conditions. Fix H1 (URL injection) and H2 (wrong store selector) before merge. The MEDIUM items (M1-M6) should be addressed in a follow-up pass.
