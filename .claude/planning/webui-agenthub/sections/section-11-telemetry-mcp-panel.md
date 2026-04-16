# Section 11: Presentation.WebUI — Telemetry and MCP Panel

## Implementation Status: COMPLETE

**Commit:** feat: implement telemetry traces panel and MCP browser (section 11)
**Tests:** 52 total (19 files) — all passing
**Deviations from plan:**
- No shadcn/ui Tabs/Select available — built simple state-based tab nav with Tailwind
- `useAgentHub.invokeToolViaAgent` already had `(conversationId, toolName, args)` 3-arg signature from section 09; `ToolInvoker` reads `conversationId` from `useChatStore`
- `telemetryStore.ts` was a stub in section 09; fully replaced here
- Zod schemas added to all API query hooks (user request: full validation at boundary)
- `MAX_CONVERSATION_SPANS = 200` added to cap per-conversation span lists
- `MAX_DEPTH = 20` guard added to `SpanNode` recursive renderer
- `encodeURIComponent(name)` applied to tool invoke URL path
- jsdom URL set to `http://localhost` in `vitest.config.ts` to enable MSW interception in tests

**Files created:**
- `src/features/telemetry/types.ts` — `SpanTreeNode` interface
- `src/features/telemetry/buildSpanTree.ts` — pure O(n) span tree builder
- `src/features/telemetry/SpanDetail.tsx` — tag key-value table
- `src/features/telemetry/SpanNode.tsx` — expandable span row with status dot + duration bar
- `src/features/telemetry/SpanTree.tsx` — root span wrapper with data-testid
- `src/features/telemetry/TracesPanel.tsx` — memoized tree + empty state + clear button
- `src/features/telemetry/RightPanel.tsx` — 5-tab panel wired to telemetry store
- `src/features/mcp/useMcpQuery.ts` — Zod-validated TanStack Query hooks (tools/resources/prompts/invoke)
- `src/features/mcp/ToolInvoker.tsx` — Direct/Via Agent toggle with pending state + null guard
- `src/features/mcp/ToolsBrowser.tsx` — two-column tool list + detail
- `src/features/mcp/ResourcesList.tsx` — display-only resource list
- `src/features/mcp/PromptsList.tsx` — display-only prompt list
- `src/features/agents/useAgentsQuery.ts` — Zod-validated agent list hook
- Test files: `buildSpanTree.test.ts`, `useTelemetryStore.test.ts`, `SpanNode.test.tsx`, `TracesPanel.test.tsx`, `ToolsBrowser.test.tsx`, `McpLists.test.tsx`

**Files modified:**
- `src/stores/telemetryStore.ts` — full implementation (was stub); added `MAX_CONVERSATION_SPANS`
- `src/components/layout/AppShell.tsx` — mount `<RightPanel />` in right slot
- `src/components/layout/Header.tsx` — wire `useAgentsQuery` + `useAppStore` into agent selector
- `src/features/chat/ChatPanel.tsx` — add `useEffect` watching `selectedAgent` for new conversation
- `src/__tests__/Header.test.tsx` — add `authConfig` mock (Header now imports apiClient transitively)
- `vitest.config.ts` — add `environmentOptions.jsdom.url: 'http://localhost'` for MSW support

---

## Overview

This section implements the right panel of the WebUI with five tabs: My Traces, All Traces, Tools, Resources, and Prompts. After this section is complete, the traces panel displays real-time span trees arriving via SignalR, the tools tab shows MCP tools with a direct or agent-routed invocation capability, and the resources/prompts tabs show their respective artifact lists as read-only displays.

**Dependencies:** Section 09 (MSAL auth, `useAgentHub`, `apiClient`) must be complete. Section 04 (`SignalRSpanExporter`, `SpanData` server type) should be complete but is not required to develop against — use the TypeScript `SpanData` type defined below.

**Can parallelize with:** section-10-chat-feature (they share the shell but operate on independent state slices and UI regions).

**Verify with:** `cd src/Content/Presentation/Presentation.WebUI && npm test`

---

## Tests First

All tests for this section live in `src/Content/Presentation/Presentation.WebUI/src/`. The test infrastructure (MSW, `renderWithProviders`) is written in Section 12 — write stubs for it here if needed, or implement a minimal local version to keep this section self-contained.

### buildSpanTree — pure function unit tests

File: `src/features/telemetry/__tests__/buildSpanTree.test.ts`

These are pure unit tests with no rendering. No setup beyond importing the function.

- `buildSpanTree returns empty array for empty input`
- `buildSpanTree nests child spans under their parent by parentSpanId`
- `buildSpanTree handles root spans with null parentSpanId`
- `buildSpanTree handles multiple disjoint trace trees`
- `buildSpanTree result is stable for same input` — call twice with the same array reference and verify the returned array reference is the same (validates memoization contract; the pure function itself should return equal-shaped output, memoization is enforced at the component level)

### useTelemetryStore — Zustand store unit tests

File: `src/features/telemetry/__tests__/useTelemetryStore.test.ts`

- `addGlobalSpan caps at MAX_GLOBAL_SPANS (500), dropping oldest entries`
- `clearAll resets both conversationSpans and globalSpans to empty`

### SpanNode — component tests

File: `src/features/telemetry/__tests__/SpanNode.test.tsx`

Requires `renderWithProviders` or a minimal wrapper.

- `SpanNode renders green status indicator for ok status`
- `SpanNode renders red status indicator for error status`
- `SpanNode renders grey status indicator for unset status`
- `Clicking SpanNode expands SpanDetail showing tags as key-value pairs`

### TracesPanel — component tests

File: `src/features/telemetry/__tests__/TracesPanel.test.tsx`

- `TracesPanel with empty spans array renders empty state placeholder text`
- `TracesPanel renders correct number of root SpanTree components for disjoint traces`

### ToolsBrowser — component tests

File: `src/features/telemetry/__tests__/ToolsBrowser.test.tsx`

Uses MSW mock returning a sample tool list (define inline if Section 12 handlers are not yet available).

- `ToolsBrowser renders tool names from MSW mock`
- `Clicking a tool shows its description and schema`
- `ToolInvoker Direct mode submit calls useInvokeTool mutation`
- `ToolInvoker Via Agent mode submit calls invokeToolViaAgent on the hub`
- `ToolInvoker shows response after successful invocation`
- `ToolInvoker shows error message after failed invocation`

### ResourcesList / PromptsList — component tests

File: `src/features/telemetry/__tests__/McpLists.test.tsx`

- `ResourcesList renders resource URI, name, and description from MSW mock`
- `PromptsList renders prompt name and description from MSW mock`

---

## File Structure

All new files under `src/Content/Presentation/Presentation.WebUI/src/`:

```
src/
  features/
    telemetry/
      useTelemetryStore.ts          # Zustand store
      buildSpanTree.ts              # pure tree-building function
      TracesPanel.tsx               # accepts spans prop, renders span trees
      SpanTree.tsx                  # renders one root span recursively
      SpanNode.tsx                  # single span row with duration bar + expand
      SpanDetail.tsx                # tags key-value table
      RightPanel.tsx                # 5-tab container
    mcp/
      useMcpQuery.ts                # TanStack Query hooks for MCP endpoints
      ToolsBrowser.tsx              # two-column tool list + detail/invoker
      ToolInvoker.tsx               # Direct/Via Agent toggle + JSON textarea
      ResourcesList.tsx             # display-only resource list
      PromptsList.tsx               # display-only prompt list
    agents/
      useAgentsQuery.ts             # TanStack Query hook for GET /api/agents
```

The `RightPanel` is mounted in the right slot of `SplitPanel` (from Section 08, `src/components/layout/SplitPanel.tsx`). The `Header` agent selector is also wired in this section.

---

## TypeScript Types

Define in `src/features/telemetry/types.ts`:

```typescript
interface SpanData {
  name: string;
  traceId: string;
  spanId: string;
  parentSpanId: string | null;   // null for root spans
  conversationId: string | null; // from agent.conversation_id tag
  startTime: string;             // ISO 8601
  durationMs: number;
  status: 'unset' | 'ok' | 'error';
  statusDescription?: string;
  kind: string;
  sourceName: string;
  tags: Record<string, string>;
}

interface SpanTreeNode extends SpanData {
  children: SpanTreeNode[];
}
```

---

## useTelemetryStore

File: `src/features/telemetry/useTelemetryStore.ts`

```typescript
const MAX_GLOBAL_SPANS = 500;

interface TelemetryState {
  conversationSpans: Record<string, SpanData[]>; // keyed by conversationId
  globalSpans: SpanData[];                        // capped at MAX_GLOBAL_SPANS
  addConversationSpan: (conversationId: string, span: SpanData) => void;
  addGlobalSpan: (span: SpanData) => void;
  clearConversation: (conversationId: string) => void;
  clearAll: () => void;
}
```

`addGlobalSpan`: appends the span then slices to the last `MAX_GLOBAL_SPANS` entries (drop oldest). Use Zustand `set` with a spread — do not mutate in place.

`addConversationSpan`: appends to `conversationSpans[conversationId]`, creating the array if absent.

The `useAgentHub` hook (Section 09, `src/hooks/useAgentHub.ts`) must be updated in this section to call `addConversationSpan` and `addGlobalSpan` from its `SpanReceived` SignalR event handler. The active `conversationId` comes from `appStore.activeConversationId`.

---

## buildSpanTree

File: `src/features/telemetry/buildSpanTree.ts`

Pure function signature:

```typescript
export function buildSpanTree(spans: SpanData[]): SpanTreeNode[]
```

Algorithm:
1. Build a map of `spanId → SpanTreeNode` (initialize each with `children: []`).
2. Iterate: if `parentSpanId` is non-null and exists in the map, push the node as a child of its parent. Otherwise it is a root node.
3. Return the array of root nodes.

The function is pure — no side effects, no store access. `TracesPanel` wraps calls in `useMemo` keyed on the `spans` array reference.

---

## TracesPanel

File: `src/features/telemetry/TracesPanel.tsx`

Props: `spans: SpanData[]`

- Wraps `buildSpanTree(spans)` in `useMemo([spans])`.
- If the tree is empty, renders a placeholder: `"No traces yet. Run an agent turn to see spans here."`.
- Otherwise renders one `SpanTree` per root node.
- A "Clear" button in the panel header calls the appropriate store clear action (`clearConversation` for My Traces tab, `clearAll` for All Traces tab).

---

## SpanTree and SpanNode

File: `src/features/telemetry/SpanTree.tsx`

Renders a single `SpanTreeNode` by delegating to `SpanNode` and recursively rendering children indented by 16px per depth level.

File: `src/features/telemetry/SpanNode.tsx`

Each node row contains:
- **Status color dot**: `w-2 h-2 rounded-full` — green (`bg-green-500`) for `ok`, red (`bg-red-500`) for `error`, grey (`bg-gray-400`) for `unset`.
- **Duration bar**: a horizontal bar whose width is proportional to `(node.durationMs / rootDurationMs) * 100%`. The `rootDurationMs` must be passed down from `SpanTree` as a prop. Same color as the status dot.
- **Span name** (text).
- **Duration** in ms appended as `(42ms)`.
- Clicking the row toggles expansion of `SpanDetail` below.

File: `src/features/telemetry/SpanDetail.tsx`

Renders all entries in `span.tags` as a two-column table (`key` / `value`). Also shows `statusDescription` if present. Wraps long values with `break-all`.

---

## RightPanel

File: `src/features/telemetry/RightPanel.tsx`

Uses shadcn/ui `Tabs` component. Tab values and their content components:

| Tab value | Label | Content |
|---|---|---|
| `my-traces` | My Traces | `<TracesPanel spans={conversationSpans[activeConversationId] ?? []} />` |
| `all-traces` | All Traces | `<TracesPanel spans={globalSpans} />` |
| `tools` | Tools | `<ToolsBrowser />` |
| `resources` | Resources | `<ResourcesList />` |
| `prompts` | Prompts | `<PromptsList />` |

The tab list (`TabsList`) is sticky at the top (`sticky top-0 z-10`). Each `TabsContent` has `overflow-y-auto` and fills available height. The panel itself is `flex flex-col h-full`.

Mount `RightPanel` in the right slot of `SplitPanel` inside `AppShell` (Section 08 file: `src/components/layout/AppShell.tsx`).

---

## Agent Selector Integration

File: `src/features/agents/useAgentsQuery.ts`

```typescript
export function useAgentsQuery() {
  return useQuery({
    queryKey: ['agents'],
    queryFn: () => apiClient.get('/api/agents').then(r => r.data),
    staleTime: 60_000,
  });
}
```

Wire the agent selector into the `Header` component (Section 08 file: `src/components/layout/Header.tsx`):
- Use shadcn/ui `Select` populated from `useAgentsQuery` data.
- Selected value stored in `appStore.selectedAgent` (add this field to the app store if not present).
- When the selected agent changes, `ChatPanel` (Section 10) reacts by starting a new conversation — this is done in `ChatPanel`'s `useEffect` watching `appStore.selectedAgent`.

---

## MCP Query Hooks

File: `src/features/mcp/useMcpQuery.ts`

Four hooks using TanStack Query and `apiClient`:

- `useToolsQuery()` — `GET /api/mcp/tools`, `staleTime: 60_000`
- `useResourcesQuery()` — `GET /api/mcp/resources`, `staleTime: 60_000`
- `usePromptsQuery()` — `GET /api/mcp/prompts`, `staleTime: 60_000`
- `useInvokeTool()` — `useMutation` posting to `POST /api/mcp/tools/{name}/invoke` with body `{ args: Record<string, unknown> }`

---

## ToolsBrowser

File: `src/features/mcp/ToolsBrowser.tsx`

Two-column layout (`grid grid-cols-[200px_1fr]`):

**Left column:** Scrollable list of tool names from `useToolsQuery`. Clicking a tool sets local state `selectedTool`. Loading and error states handled with a skeleton or error message.

**Right column:** Shows the selected tool's `name`, `description`, and `inputSchema` rendered as a `<pre>` formatted JSON block. Below the schema, renders `<ToolInvoker tool={selectedTool} />`.

File: `src/features/mcp/ToolInvoker.tsx`

Props: `tool: McpTool`

- A segmented control (two-button toggle) for "Direct" vs "Via Agent" mode.
- A JSON `<textarea>` pre-populated with `{}`. The implementer does not need to auto-generate the schema shape — empty object is sufficient for the POC.
- A Submit button.
- **Direct mode**: on submit, calls `useInvokeTool().mutate({ name: tool.name, args: JSON.parse(input) })`.
- **Via Agent mode**: on submit, calls `useAgentHub().invokeToolViaAgent(tool.name, JSON.parse(input))`. The `invokeToolViaAgent` method must be added to `useAgentHub` (Section 09) — it calls `connection.invoke('InvokeTool', toolName, args)`.
- Response or error shown in a `<pre>` block below. Parse JSON for pretty-printing; fall back to raw string on parse failure.

---

## ResourcesList and PromptsList

File: `src/features/mcp/ResourcesList.tsx`

Uses `useResourcesQuery`. Renders each resource as a card or list item with:
- `uri` (monospace, small)
- `name` (bold)
- `description` (muted text)

File: `src/features/mcp/PromptsList.tsx`

Uses `usePromptsQuery`. Renders each prompt with:
- `name` (bold)
- `description` (muted text)
- `arguments` as a comma-separated list of argument names if present

Both are display-only for the POC.

---

## Integration Points with Other Sections

- **Section 08** (`AppShell`, `SplitPanel`, `Header`): Mount `RightPanel` in the right slot. Wire `useAgentsQuery` into `Header`.
- **Section 09** (`useAgentHub`): Add `SpanReceived` handler that calls `useTelemetryStore.addConversationSpan` and `addGlobalSpan`. Add `invokeToolViaAgent` method to the hook's returned interface.
- **Section 10** (`ChatPanel`): Add `useEffect` watching `appStore.selectedAgent` to start a new conversation when the agent selection changes.
- **Section 12** (tests): MSW handlers for `/api/mcp/tools`, `/api/mcp/resources`, `/api/mcp/prompts`, and `POST /api/mcp/tools/:name/invoke` are defined there. For tests written in this section, define inline handlers if Section 12 is not yet available.
