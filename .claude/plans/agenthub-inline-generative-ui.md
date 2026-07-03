# AgentHub Inline Generative UI (Option A)

## Goal
Give the AgentHub agent (WebUI, `Presentation.WebUI`, dev server :5173) the ability to render
widgets **inline in the chat transcript** on demand: images, forms (interactive/human-in-the-loop),
and tables. Mirrors the Dashboard's proven `render_chart` pattern. No canvas/persistent-workspace
surface (that would be a separate app if ever needed).

## Decision record
- **Chosen: Option A (inline generative UI).** Widgets render inside the agent's message bubble,
  tied to the turn that produced them. Rejected Option B (main-window canvas) — AgentHub is a
  conversational console, not a co-edited-artifact workspace. If a persistent workspace need ever
  arises, it gets its own dashboard.
- A is a strict subset of B: choosing A forecloses nothing; a canvas destination could be added
  later reusing the same registry + round-trip.

## What already exists (reused as-is, zero changes)
- **Backend transport** — `AgUiClientToolBridge` (blocking proxy) + `POST /ag-ui/run` (SSE) +
  `POST /ag-ui/tool-result` (resume). Generic; tool-name agnostic. Contract:
  `ToolResultInput(string ThreadId, string CallId, string Result)`.
- **Backend tool base** — `BlockingProxyTool` owns the round-trip plumbing (client-attached check,
  timeout→ToolResult mapping, cancellation). `RenderChartTool`/`DashboardControlTool` are ~40-line
  subclasses.
- **WebUI transport** — `useAgentStream` already uses the low-level `agent.run()` observable and keeps
  the subscription open, so a mid-run tool-result POST resumes on the same stream. **No run-mode
  change required.**
- **Gating** — a tool reaches an agent only via that agent's `SKILL.md` `allowed-tools`. Generative
  widgets are opt-in per agent.

## The gap (what we build)
Almost entirely WebUI frontend. `useAgentStream` handles only `TEXT_MESSAGE_*` + `RUN_ERROR`; it
ignores `TOOL_CALL_*`. There is no component registry and no `postToolResult` helper. The chat store
models text tokens only.

---

## Backend work (small, per widget)
Three new `BlockingProxyTool` subclasses in `Infrastructure.AI/Tools/`, each ~40 lines, mirroring
`RenderChartTool`:

1. **`RenderImageTool`** (`render_image`) — non-blocking-style payload: `{ url, alt?, caption? }`.
   Validates a URL is present. Simplest — no user interaction.
2. **`RenderFormTool`** (`render_form`) — `{ title?, fields: [{ name, label, type, required?, options? }],
   submitLabel? }`. `type` restricted to a whitelist (text, textarea, number, select, checkbox, date).
   This is the interactive one; the browser's result is the filled-in values as JSON.
3. **`RenderTableTool`** (`render_table`) — `{ title?, columns: [...], rows: [[...]] }`.

Each registered in `Infrastructure.AI/DependencyInjection.Tools.cs` via keyed DI:
```
services.AddKeyedSingleton<ITool>(RenderImageTool.ToolName, (sp, _) =>
    new RenderImageTool(sp.GetRequiredService<IClientToolBridge>()));
```

Skill wiring: add the tool names to the **AgentHub agent's** `SKILL.md` `allowed-tools` (and describe
them in the skill body + `AGENT.md`, matching the dashboard-agent precedent). Locate the AgentHub
agent's SKILL.md under the agents/skills content dir (NOT the dashboard-agent one).

## Frontend work (the substance) — `Presentation.WebUI/src`
1. **`lib/agUiClient.ts`** — add `postToolResult(threadId, callId, result)` → `POST /ag-ui/tool-result`
   (mirror Dashboard). WebUI already owns conversation ids, so no `createConversation` needed.
2. **`lib/agentWidgets/registry.tsx`** (new) — the palette: `widgetType → React component`. Each entry
   is a pre-approved component (`AgentImage`, `AgentForm`, `AgentTable`). A `renderWidget(name, args)`
   lookup used by the transcript renderer. **Validate/whitelist the payload here** before rendering
   (untrusted LLM output): image URL scheme = https only; form field `type` against the whitelist;
   escape is automatic via React. Unknown widget type → a safe fallback, never a throw.
3. **Widget components** (new, `components/agent/widgets/`): `AgentImage`, `AgentForm`, `AgentTable`.
   `AgentForm` is a Reactive-style controlled form; on submit it calls back with the values.
4. **`hooks/useAgentStream.ts`** — extend the event switch:
   - `TOOL_CALL_START` → open a pending-call accumulator `{ callId, name, args:'' }`.
   - `TOOL_CALL_ARGS` → append arg deltas.
   - `TOOL_CALL_END` → parse args; for `render_image`/`render_table` render immediately and post a
     short ack as the tool result; for `render_form` render the form and **defer** the tool-result
     POST until the user submits (the blocking HITL case). Every callId must eventually post a result
     (even unmatched → an explanatory string) so the parked server tool never hangs. On POST failure,
     surface an error and stop the spinner (mirror Dashboard's `failRun`).
5. **`stores/chatStore.ts`** — extend the message model to carry an optional `widget` ({ type, args,
   and for forms a pending/submitted status}) alongside text; add tool-activity state for the "the
   agent is drawing…" affordance. Keep additions immutable (spread), matching the existing store.
6. **Transcript renderer** (`features/chat/ChatPanel.tsx` + message component) — when a message carries
   a `widget`, render it via the registry instead of / in addition to the text bubble.

## Version alignment (do first)
Bump `Presentation.WebUI` `@ag-ui/client` + `@ag-ui/core` from **0.0.53 → 0.0.57** to match
`Presentation.Dashboard`. Both pre-1.0 — verify the event/type surface didn't shift after bump
(`npm run build` + existing chat still streams).

## Testing
- **Backend** (xUnit): each tool — validation failures (missing url / bad field type), no-client-
  attached path returns graceful fail, happy-path serializes the expected payload and calls the bridge.
- **Frontend** (vitest/RTL, mirror `AgentPanel.test.tsx` / `useDashboardAgent.test.tsx`):
  - registry: unknown type → fallback; https-only URL guard; field-type whitelist.
  - `useAgentStream`: TOOL_CALL_* accumulation; render_form defers result until submit; every callId
    posts exactly one result; POST failure sets error + clears spinner.
  - `AgentForm`: renders fields, required validation, submit emits values.
- **E2E (manual, one pass):** ask the agent to "show me an image of X", "collect my preferences in a
  form", "put that in a table"; confirm inline render + form round-trip resumes the turn.

## Security notes (AI output = untrusted)
- `render_image` URL: enforce `https:` scheme (reject `javascript:`/`data:`/http). Consider a backend
  image proxy later if arbitrary external URLs are a concern (SSRF/mixed-content) — flag, don't build
  now.
- `render_form` field `type`: strict whitelist; never `eval` option lists or labels; React escaping
  covers XSS for rendered text.
- Registry is the trust boundary: the agent can only summon known widget types with validated args.

## Sequencing (one concern per PR)
1. **PR1** — version bump + `postToolResult` helper + `useAgentStream` TOOL_CALL_* handling with a
   single trivial widget (`render_image`) end-to-end, incl. backend `RenderImageTool` + skill wiring.
   Proves the full loop with the least surface.
2. **PR2** — `render_form` (the interactive/HITL round-trip) + `AgentForm` + deferred result POST.
3. **PR3** — `render_table` + any registry hardening surfaced by review.

## Open questions for Matt
- Which agent(s) get these tools? (The default AgentHub chat agent only, or a dedicated skill?)
- Starter widget set confirmed as image / form / table, or a different first three?
- Should `render_image` be restricted to model/tool-produced URLs (safer) vs. arbitrary agent-supplied
  URLs (needs the proxy)?
