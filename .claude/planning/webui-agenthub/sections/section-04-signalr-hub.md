# Section 04 — SignalR Hub (`AgentTelemetryHub`)

## Overview

This section implements `AgentTelemetryHub`, the core SignalR hub that:

- Authenticates WebSocket connections via Azure AD bearer tokens
- Routes chat turns through the MediatR pipeline with per-conversation turn serialization
- Broadcasts streaming token chunks to connected clients
- Gates global trace firehose access behind an `AgentHub.Traces.ReadAll` role claim

**Depends on:**
- Section 02 (AgentHub core wiring — `Program.cs`, DI, `AgentHubConfig`)
- Section 03 (`IConversationStore`, `ConversationRecord`, `ConversationMessage`, `ExecuteAgentTurnCommand`)

**Used by:**
- Section 05 (OTel bridge sends `SpanReceived` events through the same hub context)
- Section 07 (integration tests cover hub auth, IDOR, turn serialization, and role gate)

---

## Files to Create

| File | Purpose |
|---|---|
| `src/Content/Presentation/Presentation.AgentHub/Hubs/AgentTelemetryHub.cs` | Hub implementation |
| `src/Content/Presentation/Presentation.AgentHub/Hubs/TurnSemaphoreRegistry.cs` | Per-conversation semaphore registry (optional extract) |

---

## Tests First

Write these tests before implementing (in the `Presentation.AgentHub.Tests` project, which is created in Section 07). The tests use `TestWebApplicationFactory` and `TestAuthHandler` (also from Section 07). List them here so the implementer knows exactly what the hub must satisfy.

### Authentication and Authorization

```
Test: Unauthenticated SignalR connection is rejected
Test: StartConversation with another user's conversationId throws HubException
Test: SendMessage on another user's conversationId throws HubException
Test: JoinConversationGroup on another user's conversationId throws HubException
Test: JoinGlobalTraces without AgentHub.Traces.ReadAll role throws HubException
Test: JoinGlobalTraces with AgentHub.Traces.ReadAll role succeeds
```

### Chat Flow

```
Test: StartConversation creates a new ConversationRecord in the store
Test: StartConversation on existing conversation returns last 20 messages
Test: SendMessage dispatches ExecuteAgentTurnCommand via IMediator
Test: SendMessage emits TokenReceived events before TurnComplete
Test: SendMessage on mediator exception emits Error event with sanitized message
Test: SendMessage on mediator exception appends synthetic error message to conversation store
Test: Two rapid SendMessage calls on same conversation complete in order (no interleaved events)
```

The turn-ordering test (`Two rapid SendMessage calls`) is the most important correctness test. It verifies the `SemaphoreSlim` serialization: fire two tasks simultaneously, assert that all `TokenReceived` events from turn 1 precede all events from turn 2 in the recorded sequence.

---

## Implementation

### Hub Declaration

`AgentTelemetryHub` extends `Hub` and carries `[Authorize]`. Constructor inject:
- `IMediator _mediator`
- `IConversationStore _conversationStore`
- `ILogger<AgentTelemetryHub> _logger`
- `IOptions<AgentHubConfig> _config`

The per-conversation semaphore registry is a `ConcurrentDictionary<string, SemaphoreSlim>` private static or instance field. Use `GetOrAdd` with a factory that creates `new SemaphoreSlim(1, 1)`. Keep the dictionary on the hub instance only if the hub is registered as a singleton scoping concern — in practice, SignalR hubs are transient, so the dictionary **must** be a singleton injected via a dedicated `ConversationLockRegistry` service registered in DI, not a field on the hub class. Register it as:

```csharp
// In DependencyInjection.cs
services.AddSingleton<ConversationLockRegistry>();
```

`ConversationLockRegistry` wraps `ConcurrentDictionary<string, SemaphoreSlim>` and exposes a single method:
```csharp
/// <summary>Returns the semaphore for the given conversationId, creating it on first access.</summary>
public SemaphoreSlim GetOrCreate(string conversationId);
```

### Ownership Validation Helper

Extract a private async helper used by all conversation-scoped methods:

```csharp
/// <summary>
/// Loads the conversation and throws <see cref="HubException"/> if it does not belong
/// to the current caller. Returns null if the conversation does not yet exist.
/// </summary>
private async Task<ConversationRecord?> ValidateOwnershipAsync(string conversationId, CancellationToken ct);
```

The caller identity is obtained from `Context.UserIdentifier` (which maps to the `sub` or `oid` claim — configured by `UserIdProvider` in Section 02, or by the default SignalR user identifier). If the record exists and `record.UserId != currentUserId`, throw `new HubException("Access denied.")` — no ownership details in the message.

### `StartConversation(string agentName, string conversationId)`

1. Call `ValidateOwnershipAsync`. If conversation exists and ownership fails, throws.
2. If conversation does not exist, call `_conversationStore.CreateAsync(agentName, currentUserId, ct)` with the provided `conversationId` (or generate a new one and return it — see note below).
3. Call `Groups.AddToGroupAsync(Context.ConnectionId, $"conversation:{conversationId}")`.
4. Return the last 20 messages via `GetHistoryForDispatch(conversationId, maxMessages: 20)` so the client can restore state on reconnect.

**Note on conversationId generation:** The plan allows the client to supply a `conversationId`. If the store does not have a record for that ID, create one. If the store does have a record, validate ownership and reuse it. This supports client-generated GUIDs (idempotent reconnect).

Return type: `Task<IReadOnlyList<ConversationMessage>>` (the history, may be empty for new conversations).

### `SendMessage(string conversationId, string userMessage)`

This is the core method. Full prose spec:

1. Validate ownership via `ValidateOwnershipAsync`.
2. Acquire the per-conversation semaphore from `ConversationLockRegistry`. Use `await semaphore.WaitAsync(ct)` inside a `try/finally` to guarantee release.
3. Append the user message to the store.
4. Set `Activity.Current?.SetTag("agent.conversation_id", conversationId)` so the OTel bridge (Section 05) can route spans to the correct SignalR group.
5. Retrieve truncated history via `_conversationStore.GetHistoryForDispatch(conversationId, _config.Value.MaxHistoryMessages)`.
6. Dispatch `ExecuteAgentTurnCommand` via `_mediator.Send(...)`. The command carries `AgentName`, `UserMessage`, `ConversationHistory`, and `ConversationId`.
7. On success: chunk the response string into 50-character fixed-size segments. For each chunk, call `Clients.Caller.SendAsync("TokenReceived", new { conversationId, token = chunk, isComplete = false })`. After all chunks, send a final `TokenReceived` with `isComplete = true` and `token = fullResponse`. Then send `TurnComplete` with `conversationId`, `turnNumber` (can be the message count after appending), and `fullResponse`.
8. Append the assistant response message to the store.
9. On `Exception`: catch, log at `Error` level (full exception, structured), append a synthetic `ConversationMessage` with `Role = Assistant`, `Content = "[Error] The agent encountered an error."` to keep conversation state coherent. Send `Error` event to `Clients.Caller` with `{ conversationId, message = "An error occurred processing your request.", code = "AGENT_ERROR" }`. Never surface the exception message or stack trace to the client.
10. Release the semaphore in `finally`.

**Simulated streaming note (document with XML comment + TODO):**
```csharp
// TODO: Replace simulated 50-char chunking with real IAsyncEnumerable<string> streaming
// when ExecuteAgentTurnCommand supports it. Current implementation chunks the completed
// response after the fact, which provides the streaming UX without true streaming semantics.
```

Return type: `Task`

### `InvokeToolViaAgent(string conversationId, string toolName, string inputJson)`

Validate ownership. Construct a user message string such as `$"Please invoke the tool '{toolName}' with the following input: {inputJson}"`. Delegate to `SendMessage(conversationId, userMessage)`. Return type: `Task`.

### `JoinConversationGroup(string conversationId)`

Validate ownership. Call `Groups.AddToGroupAsync(Context.ConnectionId, $"conversation:{conversationId}")`. Return type: `Task`.

### `LeaveConversationGroup(string conversationId)`

No ownership check (leaving is always safe). Call `Groups.RemoveFromGroupAsync(Context.ConnectionId, $"conversation:{conversationId}")`. Return type: `Task`.

### `JoinGlobalTraces()`

Check `Context.User.IsInRole("AgentHub.Traces.ReadAll")`. If false, throw `new HubException("The AgentHub.Traces.ReadAll role is required.")`. Otherwise call `Groups.AddToGroupAsync(Context.ConnectionId, "global-traces")`.

Document in XML: this role must be assigned in the Azure AD app registration under **App roles**. It is intentionally absent from the default `appsettings.json` — assign it only to internal observability users.

Return type: `Task`.

### `LeaveGlobalTraces()`

Remove from `"global-traces"` group. No role check. Return type: `Task`.

---

## Server-to-Client Event Reference

These are the event names the hub sends to clients. The WebUI (Section 10) registers handlers for all of these on the SignalR connection.

| Event | Payload Fields |
|---|---|
| `TokenReceived` | `conversationId`, `token` (string), `isComplete` (bool) |
| `TurnComplete` | `conversationId`, `turnNumber` (int), `fullResponse` (string) |
| `ToolCallStarted` | `conversationId`, `spanId`, `toolName`, `input` (object) |
| `ToolCallCompleted` | `conversationId`, `spanId`, `toolName`, `output` (object), `durationMs` (long) |
| `SpanReceived` | full `SpanData` record (sent by OTel bridge in Section 05, not the hub directly) |
| `Error` | `conversationId`, `message` (sanitized string), `code` (string) |

`ToolCallStarted` and `ToolCallCompleted` are not sent by `AgentTelemetryHub` directly — they originate from the OTel bridge reading tool-call spans. The hub declares the event name constants here as `public const string` fields so the bridge and tests share them without magic strings.

---

## DI Registration

No additional hub DI beyond Section 02 (`MapHub<AgentTelemetryHub>("/hubs/agent")`). Add `ConversationLockRegistry` registration:

```csharp
// In Presentation.AgentHub/DependencyInjection.cs
services.AddSingleton<ConversationLockRegistry>();
```

---

## Key Constraints Summary

- **Ownership on every conversation-scoped method** — `StartConversation`, `SendMessage`, `InvokeToolViaAgent`, `JoinConversationGroup` all call `ValidateOwnershipAsync` before doing any work.
- **Turn serialization** — `ConcurrentDictionary<string, SemaphoreSlim>` via `ConversationLockRegistry` (singleton). Hub is transient; the registry must not be on the hub class.
- **Streaming chunks** — 50 characters fixed, not word boundaries.
- **Error sanitization** — never send exception messages or stack traces to the client. Log full exception server-side.
- **OTel tag** — `Activity.Current?.SetTag("agent.conversation_id", conversationId)` must be set before dispatching the command so Section 05 can correlate spans.
- **Role gate** — `JoinGlobalTraces` checks `AgentHub.Traces.ReadAll` role; document it must be configured in Azure AD.

---

## Verification

After implementation, run:
```
dotnet build src/AgenticHarness.slnx
dotnet test src/AgenticHarness.slnx
```

All Section 04 tests listed above must pass. Turn-ordering test is the highest-value correctness signal.
