# Foresight — Dashboard Design Spec

A calm, readable dashboard for inspecting **what an agent actually loaded, said, and did** during a session. This document explains the design approach behind it and the data shape needed to power it.

> **What's here is the WHAT.** Files, behaviors, and data contract — enough that you can wire any backend, framework, or transport to it. The HOW (fetch vs websocket, REST vs GraphQL, React vs vanilla) is your call.

---

## 1. The product, in one sentence

> Pick an agent → pick a session → see the model's context window evolve turn-by-turn, with the actual loaded content one click away.

The whole product is built around one observation: **every dashboard in this space shows you that things happened, but not what the model was looking at when it made the decision.** Foresight makes the context window the hero.

---

## 2. The four screens

| File | Purpose | Hero element |
| --- | --- | --- |
| `index.html` | Agent rail + recent-sessions table | Aggregate context composition across the visible time window |
| `session.html` | One session, timeline of turns | Per-turn context snapshot + scrub + drill-in drawer |
| `context-inspector.html` | Every loaded file in proportional category lanes | File list + live preview |
| `tool-drilldown.html` | One tool invocation (args, stdout, surrounding context) | The tool result, in-place |

`index.html` is the entry. `session.html` is the gem — everything else supports it.

---

## 3. Design language (the visual approach)

### 3.1 The segmented context bar (the load-bearing pattern)
Every screen, at every scale, uses **the same six-segment proportional rail** to represent context composition:

```
[ system ][ agents.md ][ skills ][ tools ][ mcp ][ messages ]  [— headroom —]
  cobalt   terracotta    green    violet   sky   gray            hatched
```

- **Same color, same order, same shape** everywhere. A mini-bar in a table row reads the same as the hero bar on the session page. The eye learns the language once.
- Headroom (remaining budget) is rendered as a hatched fill — visible but quiet.
- Every segment is clickable: it filters the contents panel below and lights up the matching legend tile.

This is the **one decisive flourish** the design uses. Everything else stays restrained so this single primitive can carry meaning.

### 3.2 Type & color (the active design system)
The artifact uses the **Neutral Modern** design system as-is:

- **Background:** `#FAFAFA` (light) / a derived dark surface (dark)
- **Foreground:** `#111111`
- **Accent:** `#2F6FEB` (cobalt) — used at most twice per screen
- **Type:** Inter for display + body, JetBrains Mono for every token count, ID, timestamp, code block
- **Numerics:** `font-variant-numeric: tabular-nums` everywhere so columns align like a ledger

Six category colors used **only** for the context bar and tinted state pills — never for decoration:

| Category | Token | Light hex | Use |
| --- | --- | --- | --- |
| system | `--cat-system` | cobalt #2F6FEB | the harness system prompt |
| agents | `--cat-agents` | terracotta #B45309 | personal + project agents.md, rules/ files |
| skills | `--cat-skills` | green #17A34A | loaded skills (SKILL.md bodies) |
| tools | `--cat-tools` | violet #7C3AED | tool JSON Schema definitions |
| mcp | `--cat-mcp` | sky #0284C7 | MCP server descriptions |
| messages | `--cat-messages` | gray #4B5563 | user/assistant/tool messages in the running transcript |

### 3.3 Dark/light toggle
Both modes share the same OKLch-derived palette, persist to `localStorage` (`foresight-theme`), and toggle on every page from the topbar. Dark mode is a true dark surface — not just inverted — with the same category colors at adjusted lightness.

### 3.4 Restraint rules
- **No left-border accent cards**, no purple gradients, no emoji icons. The design hits zero of the AI-slop tropes deliberately.
- **One accent per screen** — the primary CTA. Status uses semantic colors (success/warn/danger) at low alpha pills.
- **No drop shadows on inputs.** Hairline borders (1px `--border`) carry separation.
- **No more than 3 type sizes on one screen.**

---

## 4. The interaction model (session view, the gem)

This is the part that's worth understanding well — it's the design's reason for existing.

```
┌───────────────────────────────────────────────────────────────────────┐
│ HERO  Context window — at turn 4         scrub strip: ‹ ◯◯●◯◯◯◯◯◯ ›  │
│       [ system ][ agents ][ skills ][ tools ][ mcp ][ messages ]      │
│       legend tiles: each clickable, lights up matching segment        │
│       ──────────────────────────────────────────────────────────────  │
│       CONTENTS panel — populated for active category:                 │
│         · system prompt          8,200 tok   65% of cat    →          │
│         · user agents.md         4,100 tok   33% of cat    →          │
│         · ...                                                         │
│         ↑ each row clickable → opens the drawer                       │
├───────────────────────────────────────────────────────────────────────┤
│ TIMELINE                                                              │
│  ● t-01  User           14:22:08    +520 ctx                          │
│     "Refactor BillingPipeline.cs so..."                               │
│     [system][agents][·····][tools][mcp][messages]   inspect in hero ↗ │
│     loaded this turn:                                                 │
│       · User message       520 tok    [view ↗]                        │
│                                                                       │
│  ● t-02  Assistant      14:22:14    +4,540 ctx                        │
│     ...                                                               │
└───────────────────────────────────────────────────────────────────────┘
```

### 4.1 Scrub
Every turn knows the **full breakdown snapshot immediately after that turn's content lands in the model** (`ctxAfter`). Click any turn — or any dot on the growth sparkline above the timeline — and the hero rewinds to that state. The hero's title becomes "Context window — at turn N", a cobalt scrub strip appears, and the active turn's timeline node gets an accent halo. A "Jump to current" pill returns to the final state.

### 4.2 Drill-in
Click any row anywhere — in the hero contents panel, on a per-turn loaded list, on a category legend tile — and a right-side drawer slides in over a dimmed backdrop showing the **full content** of that file or message:
- Line-numbered body with hover highlight
- Lightweight syntax coloring (markdown headings bold, JSON keys cobalt)
- Sticky header (category mark + name + path) and footer (keyboard hints + prev/next)
- `Esc` to close. `↑↓←→` to walk through the current category.
- Role banner above messages (user/assistant/tool)

### 4.3 Sparkline
9 points above the timeline (boot + 8 turns), smooth curve, labels `boot · U1 · A2 · T3 · A4 · U5 · A6 · A7 · T8`. Hover any dot for the delta; click to scrub. The inflection where a skill loads is visible at a glance.

### 4.4 Why this composition
- **The hero is the answer to "what was the model looking at?"** — always one click from the actual bytes.
- **The timeline is the answer to "and when did that change?"** — the snapshot rail under each turn reads as a tiny version of the hero.
- **The drawer is the answer to "show me the words"** — no inline expand-collapse jank, no nested accordions, no leaving the page.

---

## 5. File layout

```
project-root/
├── index.html                ← agent rail + sessions table
├── session.html              ← timeline + scrub + drawer (the gem)
├── context-inspector.html    ← all loaded files in category lanes
├── tool-drilldown.html       ← one tool invocation in depth
├── foresight-dashboard-spec.md  ← this document
└── assets/
    ├── tokens.css            ← Neutral Modern design tokens + dark mode + category colors
    ├── app.css               ← shared component classes used across all four screens
    ├── app.js                ← theme toggle, formatters, shared helpers
    └── data.js               ← the SINGLE adapter point (see §6)
```

Everything is static. No build step. No framework. No CDN dependencies. Drop the folder into any web server (or open `index.html` over `file://` for local).

---

## 6. Data contract

The four screens read from `window.__OBSERVE__`, populated by `assets/data.js`. Here's the shape, field by field.

### 6.1 Top level

```ts
window.__OBSERVE__ = {
  AGENTS:          Agent[]            // for the sidebar rail
  SESSIONS:        SessionSummary[]   // for the dashboard table
  SESSION_DETAIL:  SessionDetail      // for session.html / inspector / drilldown
  CONTEXT_BUDGET:  number             // model's context window cap, e.g. 200_000
}
```

In a real app, `SESSION_DETAIL` becomes a lookup (`SESSIONS_DETAIL: Record<id, SessionDetail>`) populated on demand when the user opens a session. The current mock ships one populated detail object because all four screens are wired to the same session ID.

### 6.2 Agent

```ts
type Agent = {
  id:        string   // stable identifier, e.g. "claude-code"
  name:      string   // display name in the rail
  role:      string   // one-line subtitle, e.g. "General-purpose pair-coder"
  sessions:  number   // total session count (all-time)
  last:      string   // human-formatted recency, e.g. "8m ago"
  color:     CategoryKey   // which category color tints the avatar
  initials:  string   // 2-char avatar text
}
```

`CategoryKey` is one of: `'system' | 'agents' | 'skills' | 'tools' | 'mcp' | 'messages'`. Agents pick a color *they aren't tied to* purely for visual variety in the sidebar rail.

### 6.3 SessionSummary (one row in the dashboard table)

```ts
type SessionSummary = {
  id:         string                  // e.g. "ses_8f1c9a"
  agentId:    string                  // FK → Agent.id
  title:      string                  // human title of the work
  startedAt:  string                  // "YYYY-MM-DD HH:MM" (any locale you like)
  duration:   string                  // "3m 16s" / "1h 02m" / human
  turns:      number
  toolCalls:  number
  peakCtx:    number                  // peak tokens used at any point in session
  status:     'active' | 'completed' | 'errored'
  breakdown:  CategoryBreakdown       // final composition of the context window
}

type CategoryBreakdown = {
  system:    number   // tokens from harness system prompt
  agents:    number   // tokens from agents.md (personal + project + rules/)
  skills:    number   // tokens from loaded skills' SKILL.md bodies
  tools:     number   // tokens from tool JSON Schema definitions
  mcp:       number   // tokens from MCP server descriptions
  messages:  number   // running transcript (user + assistant + tool results)
}
```

The six keys of `CategoryBreakdown` are **load-bearing** — every visualization in the product groups tokens by them. If you want a different taxonomy, you change it here and the colors / labels propagate via `tokens.css`.

### 6.4 SessionDetail (powers `session.html`, inspector, drilldown)

```ts
type SessionDetail = {
  id:            string
  agentId:       string
  title:         string
  startedAt:     string
  lastActivity:  string                // "20 seconds ago"
  duration:      string                // "3m 16s (active)"
  model:         string                // e.g. "claude-opus-4-7"
  workdir:       string                // displayed in the side panel
  turns:         number
  toolCalls:     number
  cacheReads:    number                // for cost-efficiency stat
  cacheWrites:   number
  cost:          string                // "$0.34" — display-only, you format

  status:        'active' | 'completed' | 'errored'

  ctxBoot:       CategoryBreakdown    // state BEFORE any user input lands
  totals:        { used: number, budget: number }
  breakdown:     CategoryBreakdown    // final state == last turn's ctxAfter

  files:         LoadedFile[]         // every file/blob currently in context
  timeline:      Turn[]               // the running transcript
  tool:          ToolInvocation       // optional — feeds tool-drilldown.html
}
```

### 6.5 LoadedFile (one item the model has in context)

```ts
type LoadedFile = {
  cat:       CategoryKey              // which bar segment this contributes to
  name:      string                   // display name, e.g. "rules/testing"
  path:      string                   // source path, mono-rendered
  tokens:    number                   // size in tokens
  lang:      'markdown' | 'json' | 'text'   // for syntax styling in drawer
  excerpt:   string                   // 3-4 line preview in the contents panel
  full:      string                   // the full file body shown in the drawer
}
```

The same `LoadedFile` rows are used by:
- The hero "contents" panel under the rail (filtered to active category)
- The context-inspector category lanes (all of them at once)
- The drawer (when any row is clicked)

So if you have one good source-of-truth for what's in context, all three views populate from the same array.

### 6.6 Turn (one row in the timeline)

```ts
type Turn = {
  id:        string                    // "t-01" .. "t-NN"
  type:      'user' | 'assistant' | 'tool'   // role for the badge + body styling
  time:      string                    // "14:22:08" — display only
  role:      'user' | 'assistant' | 'tool'   // duplicate of type for forward compat
  body:      string                    // the message body (multi-line OK)
  tool?:     string                    // for type=tool, which tool name produced this
  tools?:    Array<{                   // for type=assistant, the tools invoked this turn
    name:    string                    // e.g. "Read", "Bash", "Edit"
    target:  string                    // human target, e.g. "BillingPipeline.cs"
    dur:     string                    // human duration, e.g. "42ms" / "8.4s"
  }>
  ctxAfter:  CategoryBreakdown         // snapshot immediately AFTER this turn lands
  loaded:    LoadedItem[]              // what this turn added — the per-turn delta
  unattributedTokens?: number | null   // signed reconciliation gap vs. the last model call's real
                                        // prompt (#517); positive = unattributed context, negative =
                                        // estimate overshoot; null when no model call landed this turn
}

type LoadedItem = {
  what:    string         // human label, e.g. "Read · BillingPipeline.cs"
  tokens:  number         // tokens this item adds
  cat:     CategoryKey    // which segment grows because of this item
  ref?:    string         // optional — file name to open in the drawer
                          //            (matches a LoadedFile.name)
}
```

**The invariant that powers scrub + sparkline:**

```
ctxAfter[N]  =  ctxAfter[N-1]  +  sum(loaded[N], grouped by cat)
```

The session-level `breakdown` equals the last turn's `ctxAfter`. The growth sparkline plots `Σ ctxAfter[i]` per turn.

### 6.7 ToolInvocation (feeds `tool-drilldown.html`)

```ts
type ToolInvocation = {
  name:          string                // "Bash", "Edit", etc.
  invocation:    string                // human one-liner of what was called
  turn:          string                // FK → Turn.id
  startedAt:     string
  duration:      string
  inputTokens:   number
  outputTokens:  number
  exitCode:      number
  args:          Record<string, any>   // argument dict — rendered as a key/value list
  stdout:        string                // raw output — rendered in a mono block
}
```

---

## 7. The minimum your backend has to emit

If you're wiring a real system, the **smallest useful payload** is:

1. **A list of agents** — `Agent[]` (or just synthesize from your session list if you don't have a separate agent registry).
2. **A list of recent sessions** — `SessionSummary[]`, filtered by time range. The `breakdown` is the only non-obvious field: snapshot of the model's context composition at session-end (or at peak, your call).
3. **Per-session detail on demand** — `SessionDetail` keyed by session id. This is where most of the data lives.

What you have to compute on the agent-runtime side:
- **Token counts per category**, per turn. This is the load-bearing measurement. Without it, the entire product collapses to a generic log viewer.
- **Per-turn `loaded[]` delta** — what got added this turn, with category attribution. If your runtime doesn't emit this, you can reconstruct it from `ctxAfter[N] - ctxAfter[N-1]` but the visible row labels ("Read · BillingPipeline.cs") are nicer when they come from the source.
- **Full bodies of every loaded file** — `LoadedFile.full`. If full bodies are too heavy to ship eagerly, swap to a lazy `GET /api/sessions/:id/files/:idx` and populate `full` on drawer-open.

What you don't need:
- No need to compute aggregate stats — the dashboard does its own roll-ups in JS.
- No need to ship the user's full agents.md / rules content if the same files are in context across every session — you can dedupe with content hashes and have the frontend resolve.

---

## 8. Swap pattern (mock → real)

The screens never reach across to fetch anything. They read `window.__OBSERVE__`. To switch:

1. **Strip `assets/data.js` to a loader**:
   ```js
   (async () => {
     const [AGENTS, SESSIONS] = await Promise.all([
       fetch('/api/agents').then(r => r.json()),
       fetch('/api/sessions?range=24h').then(r => r.json()),
     ]);
     const sessionId = new URL(location.href).searchParams.get('id')
                       || SESSIONS[0]?.id;
     const SESSION_DETAIL = sessionId
       ? await fetch(`/api/sessions/${sessionId}`).then(r => r.json())
       : null;
     window.__OBSERVE__ = { AGENTS, SESSIONS, SESSION_DETAIL,
                            CONTEXT_BUDGET: 200_000 };
     window.dispatchEvent(new Event('observe:ready'));
   })();
   ```

2. **Have each page listen** for `observe:ready` before running its render function. The current pages run on `DOMContentLoaded`; change them to either await the event or to call `render()` once `__OBSERVE__` is populated.

3. **Lazy file bodies** (optional): if shipping `LoadedFile.full` eagerly is too expensive, leave `full` as `null` and have the drawer open code:
   ```js
   if (!file.full) file.full = await fetch(`/api/files/${file.id}`).then(r => r.text());
   ```

4. **Live updates** (optional): if you want sessions to update while open, subscribe to an SSE or websocket stream and append to `SESSION_DETAIL.timeline`, then re-run the timeline + hero render. The growth sparkline auto-extends.

---

## 9. What the four screens do, individually

### 9.1 `index.html` — dashboard
- Topbar: brand, primary nav, theme toggle, ⌘K search stub.
- Left rail: agent list with avatar + role + recency + session count. Click → filter the table to that agent.
- Main:
  - Summary strip (4 cells): sessions today, total tool calls, peak context, cost + cache hit rate.
  - Aggregate context bar across visible sessions (the same segmented rail).
  - Sessions table with one mini-bar per row + status pill. Click any row → `session.html?id=…`.

**Reads from:** `AGENTS`, `SESSIONS`, `CONTEXT_BUDGET`.

### 9.2 `session.html` — the gem
- Topbar (same).
- Hero: the segmented context bar with scrub strip + legend + clickable contents panel.
- Growth sparkline.
- Timeline of `Turn[]` with per-turn snapshot rail + loaded list + tool chips.
- Side panel: session metadata (id, model, workdir, duration, status, cost, cache hits) + file-changes summary.
- Drawer (right-side): full file/message content, line-numbered, keyboard-navigable.

**Reads from:** `SESSION_DETAIL`, `CONTEXT_BUDGET`.

### 9.3 `context-inspector.html` — show me everything that's loaded
- Six category lanes (system / agents / skills / tools / mcp / messages), each sized proportionally to its total tokens.
- Within each lane, every `LoadedFile` row with name + tokens + share-of-category.
- Live preview pane on the right showing the active file's body.

**Reads from:** `SESSION_DETAIL.files`. Future iteration: accept a `?turn=` query param to show the state at any snapshot, not just the final.

### 9.4 `tool-drilldown.html` — one invocation in depth
- Argument list (rendered as a key/value table).
- Stdout (mono block with line numbers).
- Surrounding turn context (the user message that triggered it + the assistant turn that followed).

**Reads from:** `SESSION_DETAIL.tool` + `SESSION_DETAIL.timeline` for the surrounding context.

---

## 10. What's deliberately not built

These are intentional gaps — they keep the surface area small and let a host project bolt them on without fighting the design.

- **No real search.** The ⌘K button is a stub. Search across sessions / files / tools needs a backend index; the design leaves the affordance and waits for the real query layer.
- **No auth or user management.** The product assumes the caller is already authenticated.
- **No write actions.** This is read-only observability; no edit/retry/branch.
- **No filtering on the timeline** (mute tool-result chatter, focus on user turns) — natural next iteration.
- **No multi-agent fan-out visualization** — `Workflow` agents that spawn subagents would render as nested rails inside a turn. Hook is in the data shape (a future `Turn.subTurns: Turn[]`).
- **No diff mode** — showing `+added / -evicted` per turn (instead of cumulative state) is the natural pairing for context-compaction work.

---

## 11. Visual-language summary (one-glance reference)

| Decision | Choice | Why |
| --- | --- | --- |
| Hero primitive | One six-segment proportional rail, reused everywhere | The eye learns one language and reads every chart |
| Accent color | Cobalt `#2F6FEB`, used ≤ 2 times per screen | Restraint > ornament |
| Body font | Inter | Quiet, dense, ships with macOS/iOS as SF fallback |
| Numeric font | JetBrains Mono + tabular-nums | Columns align like a ledger |
| Card shape | 12px radius, 1px hairline border, no shadow | Whitespace > chrome |
| Status surface | Tinted-bg pills at low alpha | No screaming badges |
| Dark mode | True dark surface, same OKLch palette | Engineers want it, stakeholders open in light |
| Interaction | Click anywhere → scrub + drawer; ↑↓ everywhere | The data is the product; navigation is invisible |

That's the design. The data contract above is everything you need to feed it.
