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
2. **PR2** — `render_form` via **Approach 3** (form is UI, submit is a normal message). See the
   detailed PR2 section below — the original "deferred result POST" idea was rejected after verifying
   the framework can't suspend/resume a turn.
3. **PR3** — `render_table` + any registry hardening surfaced by review.

## Open questions for Matt
- Which agent(s) get these tools? (The default AgentHub chat agent only, or a dedicated skill?)
- Starter widget set confirmed as image / form / table, or a different first three?
- Should `render_image` be restricted to model/tool-produced URLs (safer) vs. arbitrary agent-supplied
  URLs (needs the proxy)?

---

## PR2 detail — `render_form` (Approach 3)

### Decision record — HITL round-trip model (verified 2026-07-03)
Investigated three ways for the agent to obtain form input:

- **Approach 2 (terminate-and-resume) — REJECTED, not cleanly supported.** Agent turns run through the
  framework's atomic `AIAgent.RunAsync` (`ExecuteAgentTurnCommandHandler.cs:154`), which owns the tool
  loop; the blocking proxy exists *because* of that. Conversation history is persisted text-only
  (`AgUiRunHandler.ToMeaiHistory` = role + content; no structured tool-call/result). Resuming a specific
  pending tool call across runs would require replacing the framework tool loop with a hand-rolled
  `IChatClient` loop across the whole agent path AND extending the conversation store to persist
  structured tool-call/result content. Core rewrite + correctness risk. Not a PR2 feature.
- **Approach 1 (hold the run open, long timeout) — REJECTED as primary.** Works, but the pending-call
  registry is in-memory (`PendingToolCallRegistry` = `ConcurrentDictionary` + `TaskCompletionSource`)
  and the parked `RunAsync` is tied to the live SSE request (a disconnect cancels it). A mid-form page
  refresh / disconnect loses the parked call and the submit 404s. Also needs a per-tool timeout added
  to `IClientToolBridge`.
- **Approach 3 (form is UI, submit is a message) — CHOSEN.** `render_form` is a *synchronous* client
  tool: the browser renders the form and immediately acks "Displayed the form." (millisecond round-trip,
  identical to `render_image` — no timeout, no parked run, no framework fight). The agent's turn ends
  ("I've put a form up"). When the user submits, the collected values are sent as a **normal user
  message** via the existing send path, starting a fresh turn the agent continues naturally. Survives
  refresh (the form is UI; the submit is an ordinary persisted message); reuses existing infrastructure.
  Trade-off: the agent receives answers as the user's next message rather than a formal tool result —
  invisible in practice, the conversation context makes the linkage obvious.

### Backend
- `RenderFormTool` (new, `BlockingProxyTool` subclass, mirrors `RenderImageTool`). Params:
  `{ title?, fields: [{ name, label?, type, required?, options? }], submitLabel? }`. Validate: `fields`
  non-empty; each field has a non-empty `name` and a `type` in the whitelist
  {`text`, `textarea`, `number`, `select`, `checkbox`, `date`}; `select` requires non-empty `options`.
  Fail with a clear message otherwise. Serialize params to the client; ack "Displayed the form."
- Register via keyed DI next to `render_image`. Add `render_form` to the `research-agent` skill
  `allowed-tools` + a one-line usage note (same home as `render_image`; the default-agent-skill
  follow-up still stands and should be resolved before a widget accretes onto a third skill).

### Frontend (WebUI)
- `widgets/formTypes.ts` (new) — `FormFieldSpec`, `FormSpec`, and `parseFormArgs(args)` at the client
  trust boundary: coerce/validate, drop invalid fields, unknown `type` skipped; return a typed
  `FormSpec` or a reason. Field-type whitelist enforced here too (defense in depth).
- `widgets/AgentForm.tsx` (new) — renders the form from a `FormSpec`: one control per field by type,
  required-field validation, a submit button (`submitLabel`). On submit: format values into a readable
  message, call the shared send hook, then mark submitted (disable inputs/button, show "Submitted") to
  prevent double-send.
- `hooks/useSendUserMessage.ts` (new) — extract the existing inline send sequence
  (`addMessage(user)` → `startStreaming()` → `agUiSend`) currently duplicated in `ChatPanel`
  (`handleSuggestionClick`, main send) into one reusable hook that resolves the active conversation id.
  Refactor `ChatPanel` to use it (DRY); `AgentForm` uses it for submit — this is why the form neither
  duplicates `ChatPanel` nor prop-drills a callback through the widget registry.
- `widgets/registry.tsx` — add `render_form` → `<AgentForm ... />` (Map entry).
- `hooks/useAgentStream.ts` — add a `render_form` branch to the `finishToolCall` dispatch: render the
  form widget message + ack "Displayed the form." (synchronous — same shape as `render_image`).
- `useChatStore` — no change; `AgentWidget { type, args }` already carries the form spec in `args`.

### Message format on submit
Readable text the model parses naturally and that renders cleanly as the user's message:
```
Here are my answers:
- Full name: Jane Doe
- Email: jane@example.com
- Newsletter: Yes
```
Labels fall back to field `name`; checkbox → Yes/No; empty optional fields omitted.

### Tests
- Backend `RenderFormTool`: metadata; unknown op; no-client; empty/missing fields; invalid field type;
  `select` without options; happy-path serialization + ack.
- Frontend: `parseFormArgs` (whitelist, required, select options, drop invalid); `AgentForm` (renders
  each field type, required validation blocks submit, submit formats + calls send hook + disables
  after); `useSendUserMessage` (adds user message + triggers a run); registry `render_form` → AgentForm;
  `useAgentStream` `render_form` branch (renders widget + acks).

### Note on the deferred registry-unification cleanup
Approach 3 makes `render_form`'s tool round-trip **synchronous**, so the reason we deferred folding
`handle` into the widget registry in PR1 (render_form's async submit) no longer applies. With two
synchronous widgets, the inline `finishToolCall` dispatch stays (rule of three); unify render+handle
into a single widget registry at **PR3** (`render_table`) when the shape is proven across three.

### Out of scope (tracked follow-ups)
- Registry render+handle unification → PR3.
- `default`-agent dedicated skill home (generative UI grafted onto `research-agent`).
