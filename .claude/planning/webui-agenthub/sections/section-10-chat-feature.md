# Section 10: Presentation.WebUI — Chat Feature

## Overview

This section builds the left panel of the WebUI: the chat message list, streaming token rendering, typing indicator, and chat input form. After completing this section, a user can type a message, see it appear optimistically, watch the agent respond with streaming tokens, and scroll through the full conversation history.

**Dependencies:** Section 09 (MSAL Auth and API Client) must be complete. The `useAgentHub` hook, `buildHubConnection`, `apiClient`, and the app shell layout are all assumed to exist.

**Can parallelize with:** Section 11 (Telemetry and MCP Panel).

**Verify with:** `cd src/Content/Presentation/Presentation.WebUI && npm test`

---

## Tests First

Write these tests before implementing. All tests live in `src/features/chat/__tests__/` unless noted. Use `renderWithProviders` from `src/test/utils.tsx` (established in Section 12, but you can stub it here) and the Vitest + RTL stack.

### useChatStore

File: `src/features/chat/__tests__/useChatStore.test.ts`

- `appendToken accumulates tokens in streamingContent` — call `appendToken('hello ')` then `appendToken('world')`, assert `streamingContent === 'hello world'` and `isStreaming === true`
- `finalizeStream clears streamingContent and adds message to messages array` — after accumulating tokens, call `finalizeStream('hello world')`, assert `streamingContent === ''`, `isStreaming === false`, and `messages` contains an assistant message with `content === 'hello world'`
- `clearMessages resets all state` — populate messages and streaming state, call `clearMessages()`, assert store returns to initial shape

### ChatInput

File: `src/features/chat/__tests__/ChatInput.test.tsx`

- `ChatInput submit calls sendMessage with input value` — render with a mock `sendMessage` prop, type a message, submit the form, assert mock called with correct value
- `ChatInput is disabled while isStreaming is true` — set store `isStreaming: true`, assert textarea and button have `disabled` attribute
- `ChatInput clears after submit` — after successful submit, assert textarea value is empty
- `ChatInput rejects empty string (does not call sendMessage)` — submit with empty input, assert mock never called
- `ChatInput rejects messages over 4000 characters` — submit a 4001-character string, assert mock never called and validation error is visible

### MessageList

File: `src/features/chat/__tests__/MessageList.test.tsx`

- `MessageList renders all messages from the store` — seed store with 3 messages, render `MessageList`, assert all 3 appear in the DOM
- `MessageList renders user message right-aligned` — seed a user message, assert the element has a CSS class or style indicating right alignment
- `MessageList renders assistant message left-aligned` — seed an assistant message, assert left-aligned class
- `Streaming: TokenReceived event updates visible text in DOM` — set `streamingContent: 'partial'` in store, render `MessageList`, assert `'partial'` appears in DOM
- `TurnComplete event finalizes assistant message and hides TypingIndicator` — simulate `finalizeStream`, assert `TypingIndicator` is not in the DOM and the finalized message is visible

### TypingIndicator

File: `src/features/chat/__tests__/TypingIndicator.test.tsx`

- `TypingIndicator is visible when isStreaming is true` — set store `isStreaming: true`, render `TypingIndicator`, assert it is in the document
- `TypingIndicator is not rendered when isStreaming is false` — set store `isStreaming: false`, assert component is not rendered or has `hidden` attribute

---

## Files Created / Modified (Actual)

**New files:**
```
src/features/chat/
  useChatStore.ts         # Zustand store (includes error/setError)
  ChatPanel.tsx           # Root left-panel with ErrorBanner component
  MessageList.tsx         # Scroll div + scrollIntoView (NOT react-window — see deviations)
  MessageItem.tsx         # Individual message renderer
  TypingIndicator.tsx     # Animated dots
  ChatInput.tsx           # RHF + Zod form with try/catch on sendMessage
  __tests__/
    useChatStore.test.ts
    ChatInput.test.tsx
    MessageList.test.tsx  # No react-window mock needed
    TypingIndicator.test.tsx
src/components/ui/textarea.tsx   # Native textarea wrapper (no @base-ui)
```

**Modified files:**
```
src/stores/chatStore.ts          # Re-export barrel → @/features/chat/useChatStore
src/hooks/useAgentHub.ts         # TurnComplete: finalizeStream(message.content) not message
src/components/layout/AppShell.tsx  # Wired ChatPanel as left panel
src/__tests__/App.test.tsx       # Added useAgentHub mock (prevents SignalR in tests)
src/test/setup.ts                # Added Element.prototype.scrollIntoView = vi.fn()
```

## Deviations from Plan

1. **MessageList: no react-window** — `react-window` installed is v2 which has a completely rewritten API (`List`/`RowComponentProps`, no `VariableSizeList`). Rather than fight the new API for a POC, replaced with a simple `overflow-y-auto` div + `scrollIntoView` on a bottom sentinel `<div ref={bottomRef}>`. Scroll dep array uses `streamingContent` to scroll on every token during streaming.

2. **useChatStore includes `error`/`setError`** — `useAgentHub` needed error propagation for SignalR failures and the `Error` server event. Added `error: string | null` and `setError(msg: string | null)` to `ChatState`. `clearMessages` also resets error.

3. **ErrorBanner in ChatPanel** — New inline component renders a dismissible red banner when `useChatStore.error` is non-null.

4. **textarea.tsx is a native element wrapper** — `@base-ui/react` doesn't ship a textarea component; created a minimal shadcn-style wrapper using native `<textarea>` with Tailwind classes.

5. **ChatInput.onSubmit wraps sendMessage in try/catch** — Unhandled promise rejections avoided; failures route to `useChatStore.setError`.

6. **TurnComplete payload is `{ content: string }` not `ChatMessage`** — Fixed in `useAgentHub.ts` to call `finalizeStream(message.content)`.

## Test Count: 31 total (13 files)

---

## Implementation Details

### useChatStore — `src/features/chat/useChatStore.ts`

Zustand store. State shape:

```typescript
interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  timestamp: Date;
  toolCalls?: ToolCallSummary[];
}

interface ChatState {
  conversationId: string | null;
  messages: ChatMessage[];
  isStreaming: boolean;
  streamingContent: string;  // accumulated tokens for the active stream
}
```

Actions: `setConversationId(id)`, `addMessage(msg: ChatMessage)`, `appendToken(token: string)`, `finalizeStream(fullResponse: string)`, `clearMessages()`.

`appendToken` sets `isStreaming: true` and concatenates onto `streamingContent`. `finalizeStream` sets `isStreaming: false`, clears `streamingContent`, and appends a new assistant `ChatMessage` with the provided `fullResponse` as `content`. `clearMessages` resets everything to initial state (preserves `conversationId`).

`useAgentHub`'s event handlers (already established in Section 09) wire to this store: `TokenReceived` → `appendToken`, `TurnComplete` → `finalizeStream`.

### ChatPanel — `src/features/chat/ChatPanel.tsx`

Root container for the left panel. Renders: `ConversationHeader`, `MessageList`, `TypingIndicator` (conditional on `isStreaming`), `ChatInput`.

On mount, reads `conversationId` from `useChatStore`. If `null`, generates a new `crypto.randomUUID()` and calls `setConversationId`. Then calls `useAgentHub().startConversation(selectedAgent, conversationId)` where `selectedAgent` comes from `appStore.selectedAgent`.

`ConversationHeader` is a small inline component showing the active `conversationId` (truncated) and a "Clear" button that calls `clearMessages()`.

### MessageList — `src/features/chat/MessageList.tsx`

Uses `react-window`'s `VariableSizeList` for virtualization. The list reads `messages` from `useChatStore`. Each item renders `MessageItem`.

Appends a synthetic streaming item at the end of the list when `isStreaming === true` and `streamingContent` is non-empty — renders `streamingContent` as an assistant message in progress.

Scrolls to the bottom on new messages using `listRef.current.scrollToItem(messages.length - 1, 'end')` in a `useEffect` keyed on `messages.length`.

Row height estimation: use a fixed estimate (e.g. 80px) for the `itemSize` callback as a POC simplification; `VariableSizeList` will reflow on measurement.

### MessageItem — `src/features/chat/MessageItem.tsx`

Accepts `message: ChatMessage` and `isStreaming?: boolean` props.

User messages: right-aligned, distinct background (use Tailwind `bg-primary text-primary-foreground ml-auto rounded-lg p-3 max-w-[80%]`).

Assistant messages: left-aligned (use `bg-muted mr-auto rounded-lg p-3 max-w-[80%]`).

Code block rendering: split `content` on the regex `` /```[\w]*\n([\s\S]*?)```/g `` — segments between fences render in a `<pre><code>` block; other segments render as plain `<p>`. No markdown library needed.

Tool call summaries: if `toolCalls` is non-empty, render each as a small collapsed chip below the message body. A chip shows the tool name; clicking expands inline to show input/output JSON. Keep this simple — a `useState` toggle per chip.

### TypingIndicator — `src/features/chat/TypingIndicator.tsx`

Three animated dots using Tailwind `animate-bounce` with staggered delays (`delay-0`, `delay-150`, `delay-300`). Renders only when `useChatStore(s => s.isStreaming)` is true; return `null` otherwise so it does not occupy space.

### ChatInput — `src/features/chat/ChatInput.tsx`

React Hook Form with `zodResolver`. Zod schema:

```typescript
const schema = z.object({
  message: z.string().min(1, 'Message is required').max(4000, 'Message too long'),
});
```

On valid submit: call `useAgentHub().sendMessage(conversationId, data.message)`, call `form.reset()`. The form is entirely `disabled` while `isStreaming === true`.

Keyboard behaviour: Enter submits, Shift+Enter inserts a newline. Use `onKeyDown` on the `Textarea` — call `e.preventDefault()` and `form.handleSubmit(onSubmit)()` when `e.key === 'Enter' && !e.shiftKey`.

Use shadcn/ui `Textarea` and `Button` components. Show character count `{watchedMessage.length}/4000` below the textarea.

---

## Streaming Token Rendering

The active streaming response renders as the last item in `MessageList` using `streamingContent`. When `TurnComplete` fires, `finalizeStream` replaces `streamingContent` with a permanent `ChatMessage` in `messages`. This means the streaming item and the finalized item are the same visual position — no content jump.

The `TypingIndicator` is shown alongside the streaming content (not instead of it) so the user sees both partial text and the animation.

---

## Dependencies (from prior sections)

- `useAgentHub` hook (`src/hooks/useAgentHub.ts`) — exposes `sendMessage`, `startConversation`. Event handlers for `TokenReceived` and `TurnComplete` must already dispatch to `useChatStore` actions. If not yet wired in Section 09, add those two `connection.on(...)` registrations in `useAgentHub` as part of this section.
- `appStore.selectedAgent` (`src/stores/appStore.ts`) — read in `ChatPanel` to determine which agent to start.
- shadcn/ui `Textarea`, `Button`, `Badge` — must be present in `src/components/ui/`.
- `react-window` and `react-hook-form` and `zod` npm packages — installed in Section 01 scaffolding. Verify they are in `package.json` before starting; add them if missing.

---

## Acceptance Criteria

- All 14 tests listed above pass.
- A user can type a message and press Enter; the message appears in the list immediately.
- While the agent is responding, `TypingIndicator` is visible and token text accumulates in the chat window.
- After `TurnComplete`, the final message is stable in the list and `TypingIndicator` is hidden.
- Messages containing code fences render inside `<pre><code>` blocks.
- The input form is disabled (textarea + button) while streaming.
- `npm test` exits 0.
