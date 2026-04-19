# Section-10 Code Review Interview

## Findings Triage

### H1 — XSS invariant undocumented (MessageItem.tsx)
**Decision: AUTO-FIX**
Added security comment above `parseContent` explaining React JSX auto-escaping is the XSS defense and that DOMPurify must be added before any markdown renderer migration.

### H2 — sendMessage errors swallowed (ChatInput.tsx)
**Decision: AUTO-FIX**
Wrapped `sendMessage` in try/catch in `onSubmit`. Errors now call `useChatStore.getState().setError(...)` so the ErrorBanner picks them up.

### M1 — clearMessages does not reset error state (useChatStore.ts)
**Decision: AUTO-FIX**
Added `error: null` to the `clearMessages` set call.

### M2 — Stale closure in ChatPanel useEffect (empty deps)
**Decision: LET GO**
The empty dep array + `eslint-disable` is intentional: we want a single conversation started once on mount, not re-started when agent selection changes. Agent switching would need a separate flow (clear + new conversation) that's out of scope for this section.

### M3 — Auto-scroll misses streaming updates (MessageList.tsx)
**Decision: AUTO-FIX**
Changed `useEffect` deps from `[messages.length, hasStreamingItem]` to `[messages.length, streamingContent]`. Now each new token triggers a scroll-to-bottom.

### M4 — ToolCallChip index key (MessageItem.tsx)
**Decision: LET GO**
Tool calls on a completed message are immutable and never reorder, so index key is stable. Acceptable for POC.

### M5 — Error state not rendered (ChatPanel.tsx)
**Decision: AUTO-FIX**
Added `ErrorBanner` component to ChatPanel that renders dismissible error from store when `error !== null`.

### M6 — watchedMessage.length undefined guard (ChatInput.tsx)
**Decision: LET GO**
`defaultValues: { message: '' }` guarantees `watchedMessage` is never undefined. No guard needed.

### I3 — ConversationHistory timestamp string→Date mismatch
**Decision: NOTE**
Server sends JSON with ISO string timestamps; ChatMessage.timestamp is typed as Date. At runtime the field will be a string for ConversationHistory messages. No date arithmetic is done, so no crash — just a type lie. Will be formally addressed in section-12 (MSW handlers will produce proper Date objects via JSON.parse revival or explicit mapping).

## Applied Fixes Summary
1. `MessageItem.tsx` — XSS security comment added above `parseContent`
2. `ChatInput.tsx` — try/catch wrapping sendMessage, setError on failure
3. `useChatStore.ts` — clearMessages resets error to null
4. `MessageList.tsx` — scroll deps: `streamingContent` replaces `hasStreamingItem`
5. `ChatPanel.tsx` — ErrorBanner component added, wired below ConversationHeader
