# Section 03: Presentation.AgentHub — Conversation Store and Agent Execution

## Overview

This section adds the conversation persistence layer and the HTTP controller for managing conversations. It produces the `IConversationStore` interface, `FileSystemConversationStore` implementation, domain types (`ConversationMessage`, `ConversationRecord`, `ToolCallRecord`), and `AgentsController`.

After this section, the agent hub can persist conversation history to disk, enforce user ownership, and return conversation lists and detail via REST. The SignalR hub (section 04) depends on `IConversationStore` directly, so this section must be complete before section 04 begins.

**Verify command:** `dotnet test src/AgenticHarness.slnx`

---

## Dependencies

- **section-01-scaffolding** — project files and solution registration must exist
- **section-02-agenthub-core** — `AgentHubConfig`, DI extension method, and `Program.cs` middleware pipeline must be in place; `IConversationStore` is registered there via `AddAgentHubServices`

---

## File Locations (Actual — as built)

All files are under `src/Content/Presentation/Presentation.AgentHub/`.

```
Presentation.AgentHub/
  Models/
    ConversationMessage.cs
    ConversationRecord.cs
    ToolCallRecord.cs
    MessageRole.cs
    AgentSummary.cs
  Interfaces/
    IConversationStore.cs
  Services/
    FileSystemConversationStore.cs
  Controllers/
    AgentsController.cs        ← updated (was stub)
  Extensions/
    ClaimsPrincipalExtensions.cs   ← new (GetUserId reads OID claim)
  DependencyInjection.cs         ← updated (IConversationStore registered)
```

Test files are under `src/Content/Tests/Presentation.AgentHub.Tests/`:

```
Presentation.AgentHub.Tests/
  ConversationStore/
    FileSystemConversationStoreTests.cs   ← 9 unit tests, all passing
  Controllers/
    AgentsControllerTests.cs              ← 4 stubs (wired in section-07)
```

---

## Tests First (Actual outcome)

9 `FileSystemConversationStore` unit tests implemented and passing. `AgentsController` tests are stubs (empty `await Task.CompletedTask` bodies) — wired in section-07 once `TestAuthHandler` supports per-test claim injection.

**Deviations from plan:**
- `ClaimsPrincipalExtensions.cs` placed in `Extensions/` folder (plan didn't specify exact folder, `Extensions/` follows project convention).
- `AgentsController` ownership checks cache `var callerId = User.GetUserId()` before use (code review fix — avoids double call).
- `AppendMessageAsync` checks file existence before `ReadAllTextAsync` (code review fix — cleaner error for missing conversation).
- Warning logs include `{OwnerId}` with explicit audit comment (user-confirmed intentional).

**Test count:** 22 total (9 new store tests + 4 new controller stubs + 6 CoreSetupTests + 2 ScaffoldTests + 1 other).

### FileSystemConversationStoreTests.cs

```csharp
/// <summary>Unit tests for FileSystemConversationStore using a temp directory fixture.</summary>
public class FileSystemConversationStoreTests : IDisposable
{
    // Arrange: create a temp directory, construct FileSystemConversationStore pointing at it.
    // Dispose: delete the temp directory.

    [Fact] public async Task CreateAsync_WritesJsonFileAtExpectedPath() { }
    [Fact] public async Task GetAsync_ReturnsNullForUnknownConversationId() { }
    [Fact] public async Task GetAsync_DeserializesConversationRecordCorrectly() { }

    /// <summary>Write to .tmp first, then move — verify the final file exists and no .tmp remains.</summary>
    [Fact] public async Task AppendMessageAsync_UpdatesFileAtomically() { }

    [Fact] public async Task DeleteAsync_RemovesTheFile() { }

    /// <summary>ListAsync must filter by userId — create two conversations with different userIds, assert only the correct one is returned.</summary>
    [Fact] public async Task ListAsync_ReturnsOnlyConversationsBelongingToGivenUserId() { }

    /// <summary>Fire N concurrent AppendMessageAsync calls on the same conversationId, assert final message count equals N.</summary>
    [Fact] public async Task ConcurrentAppendMessageAsync_OnSameConversationId_DoesNotCorruptFile() { }

    /// <summary>Pass a conversationId containing "../" or absolute path segments, assert ArgumentException.</summary>
    [Fact] public async Task PathWithTraversalCharacters_ThrowsArgumentException() { }

    [Fact] public async Task GetHistoryForDispatch_ReturnsAtMostMaxMessages() { }

    /// <summary>Seed 30 messages, request last 10, assert the returned messages are the final 10 (not the first 10).</summary>
    [Fact] public async Task GetHistoryForDispatch_ReturnsLastNMessages_NotFirstN() { }
}
```

### AgentsControllerTests.cs

```csharp
/// <summary>
/// Integration tests for AgentsController ownership enforcement.
/// Uses TestWebApplicationFactory (section 07) — wire factory once available.
/// </summary>
public class AgentsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    [Fact] public async Task GetConversations_ReturnsOnlyConversationsOwnedByAuthenticatedUser() { }
    [Fact] public async Task GetConversationById_AnotherUsersConversation_Returns403() { }
    [Fact] public async Task DeleteConversation_AnotherUsersConversation_Returns403() { }
    [Fact] public async Task DeleteConversation_OwnConversation_Returns204AndRemovesFile() { }
}
```

---

## Domain Types

### `MessageRole.cs`

Enum with values: `User`, `Assistant`, `System`, `Tool`.

### `ToolCallRecord.cs`

Immutable record:

```csharp
/// <summary>Captures a single tool invocation within an assistant turn.</summary>
public sealed record ToolCallRecord(
    string ToolName,
    JsonElement Input,
    JsonElement Output,
    long DurationMs);
```

### `ConversationMessage.cs`

Immutable record:

```csharp
/// <summary>A single message in a conversation. Role determines rendering behavior in the UI.</summary>
public sealed record ConversationMessage(
    MessageRole Role,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<ToolCallRecord>? ToolCalls = null);
```

### `ConversationRecord.cs`

Immutable record:

```csharp
/// <summary>
/// Full conversation state persisted to disk. UserId is the object ID (OID claim)
/// of the owning Azure AD user. Never expose records to users other than the owner.
/// </summary>
public sealed record ConversationRecord(
    string Id,
    string AgentName,
    string UserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConversationMessage> Messages);
```

### `AgentSummary.cs`

```csharp
/// <summary>Lightweight DTO returned by GET /api/agents.</summary>
public sealed record AgentSummary(string Name, string Description);
```

---

## IConversationStore Interface

File: `Interfaces/IConversationStore.cs`

```csharp
/// <summary>
/// Persistent store for conversation records. Thread-safe for concurrent access.
/// Implementations must enforce user-ownership isolation — callers are responsible
/// for checking ConversationRecord.UserId against the authenticated user before
/// returning records to clients.
/// </summary>
public interface IConversationStore
{
    /// <summary>Returns null if no conversation with the given ID exists.</summary>
    Task<ConversationRecord?> GetAsync(string conversationId, CancellationToken ct = default);

    /// <summary>
    /// Returns all conversations owned by userId.
    /// O(n) in the number of stored conversations — acceptable for POC scale.
    /// </summary>
    Task<IReadOnlyList<ConversationRecord>> ListAsync(string userId, CancellationToken ct = default);

    /// <summary>Creates a new conversation with a generated GUID id.</summary>
    Task<ConversationRecord> CreateAsync(string agentName, string userId, CancellationToken ct = default);

    /// <summary>Appends a message to an existing conversation record.</summary>
    Task AppendMessageAsync(string conversationId, ConversationMessage message, CancellationToken ct = default);

    /// <summary>Permanently deletes a conversation record.</summary>
    Task DeleteAsync(string conversationId, CancellationToken ct = default);

    /// <summary>
    /// Returns the last <paramref name="maxMessages"/> messages from the conversation.
    /// Called by the hub before dispatching ExecuteAgentTurnCommand to prevent
    /// unbounded token growth. Returns null if the conversation does not exist.
    /// </summary>
    Task<IReadOnlyList<ConversationMessage>?> GetHistoryForDispatch(
        string conversationId,
        int maxMessages,
        CancellationToken ct = default);
}
```

---

## FileSystemConversationStore

File: `Services/FileSystemConversationStore.cs`

```csharp
/// <summary>
/// File-system-backed conversation store. Each ConversationRecord is stored as a
/// JSON file at {ConversationsPath}/{conversationId}.json.
///
/// Thread safety: a single SemaphoreSlim(1,1) serializes all file I/O. This is
/// intentionally simple for POC scale. A production implementation should use
/// AsyncKeyedLock (or similar) for per-conversation-id locking to allow
/// concurrent operations across different conversations.
///
/// Atomic writes: all writes go to a .tmp file first, then File.Move(..., overwrite:true).
/// This prevents partial-write corruption if the process exits mid-write.
///
/// Path safety: the constructor resolves ConversationsPath to an absolute path.
/// Any operation whose computed file path does not start with this base path throws
/// ArgumentException, preventing path-traversal attacks via crafted conversationIds.
/// </summary>
public sealed class FileSystemConversationStore : IConversationStore
{
    private readonly string _basePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<FileSystemConversationStore> _logger;

    public FileSystemConversationStore(
        IOptions<AgentHubConfig> config,
        ILogger<FileSystemConversationStore> logger)
    {
        _basePath = Path.GetFullPath(config.Value.ConversationsPath);
        _logger = logger;
        Directory.CreateDirectory(_basePath);
    }

    // ResolveAndValidatePath: compute full path, throw ArgumentException if it
    // does not start with _basePath.

    // GetAsync, ListAsync, CreateAsync, AppendMessageAsync, DeleteAsync:
    // All acquire _lock, read/write via atomic tmp→move pattern, release on exit.

    // GetHistoryForDispatch: calls GetAsync, returns last maxMessages from Messages.
    // Returns null if conversation not found.
}
```

Key implementation notes:

- Use `System.Text.Json` with `JsonSerializerOptions` that include `JsonStringEnumConverter` so `MessageRole` serializes as a string, not an integer.
- `CreateAsync` sets `Id = Guid.NewGuid().ToString()`, `CreatedAt = UpdatedAt = DateTimeOffset.UtcNow`, and `Messages = []`.
- `AppendMessageAsync` reads the existing record, builds a new record `with { Messages = [..existing, newMessage], UpdatedAt = DateTimeOffset.UtcNow }`, then writes atomically.
- `ListAsync` enumerates all `*.json` files (not `.tmp`), deserializes each, and filters by `UserId`. Log a warning and skip (do not throw) on any file that fails to deserialize — defensive against partial corruption.

---

## AgentsController

File: `Controllers/AgentsController.cs`

```csharp
/// <summary>
/// Manages agent discovery and conversation history.
/// All endpoints require authentication. Ownership is enforced at the conversation level:
/// a user may only access or delete conversations where ConversationRecord.UserId
/// matches their own identity claim.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public sealed class AgentsController : ControllerBase
{
    // Constructor: IConversationStore, IOptions<AgentHubConfig>, ILogger<AgentsController>

    /// <summary>Returns the list of configured agents.</summary>
    [HttpGet("agents")]
    public IActionResult GetAgents() { }

    /// <summary>Returns all conversations owned by the current user.</summary>
    [HttpGet("conversations")]
    public async Task<IActionResult> GetConversations(CancellationToken ct) { }

    /// <summary>Returns a single conversation. 404 if not found. 403 if not owned by caller.</summary>
    [HttpGet("conversations/{id}")]
    public async Task<IActionResult> GetConversation(string id, CancellationToken ct) { }

    /// <summary>Deletes a conversation. 403 if not owned by caller. 204 on success.</summary>
    [HttpDelete("conversations/{id}")]
    public async Task<IActionResult> DeleteConversation(string id, CancellationToken ct) { }
}
```

**Ownership enforcement pattern** — apply identically in `GetConversation` and `DeleteConversation`:

```csharp
var record = await _store.GetAsync(id, ct);
if (record is null) return NotFound();
if (record.UserId != User.GetUserId()) return Forbid();
```

`User.GetUserId()` is an extension method on `ClaimsPrincipal` that reads the `oid` claim (Azure AD object ID). Add this as a static extension in a `ClaimsPrincipalExtensions.cs` file alongside the controller, or in a shared `Extensions/` folder if one already exists.

**`GetAgents` implementation:** Read agent names from `AppConfig:AI:AgentFramework` via the injected `IOptions<AgentHubConfig>` (or a dedicated `IOptions<AgentsConfig>` if the existing config hierarchy has one). For the POC, returning `[new AgentSummary(config.DefaultAgentName, "Default agent")]` is acceptable. Document with a TODO that this should enumerate the full agent registry when available.

---

## DI Registration (update section 02's DependencyInjection.cs)

In `AddAgentHubServices`, add:

```csharp
services.AddSingleton<IConversationStore, FileSystemConversationStore>();
```

`FileSystemConversationStore` is registered as a singleton because it manages a single `SemaphoreSlim` for thread-safety. Using `AddScoped` or `AddTransient` would create multiple semaphore instances and break the concurrency guarantee.

---

## AgentHubConfig (reference from section 02)

`FileSystemConversationStore` reads `ConversationsPath` from `AgentHubConfig`. The config record (defined in section 02) must have:

```csharp
public sealed record AgentHubConfig(
    string ConversationsPath,   // default: "./conversations"
    string DefaultAgentName,
    int MaxHistoryMessages,     // default: 20
    AgentHubCorsConfig Cors);
```

`MaxHistoryMessages` is consumed by the hub (section 04) when calling `GetHistoryForDispatch`. It is declared on `AgentHubConfig` so both the store and the hub share the same configured value.

---

## appsettings.json additions

In `appsettings.json` (established in section 02), ensure the `AgentHub` block includes:

```json
"AgentHub": {
  "ConversationsPath": "./conversations",
  "DefaultAgentName": "default",
  "MaxHistoryMessages": 20,
  "Cors": {
    "AllowedOrigins": [ "http://localhost:5173" ]
  }
}
```

---

## Error Handling Notes

- `FileSystemConversationStore` methods throw `ArgumentException` for path traversal — controllers must not catch this; let the framework return 400.
- `AgentsController` returns `Forbid()` (403), not `Unauthorized()` (401), for ownership violations. The user is authenticated; they are simply not authorized to access that resource.
- Never surface file paths, stack traces, or internal exception messages to HTTP clients.
