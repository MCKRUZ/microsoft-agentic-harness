diff --git a/src/Content/Presentation/Presentation.AgentHub/Controllers/AgentsController.cs b/src/Content/Presentation/Presentation.AgentHub/Controllers/AgentsController.cs
index 501b57c..91dd28b 100644
--- a/src/Content/Presentation/Presentation.AgentHub/Controllers/AgentsController.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/Controllers/AgentsController.cs
@@ -1,18 +1,90 @@
 using Microsoft.AspNetCore.Authorization;
 using Microsoft.AspNetCore.Mvc;
+using Microsoft.Extensions.Options;
+using Presentation.AgentHub.Extensions;
+using Presentation.AgentHub.Interfaces;
+using Presentation.AgentHub.Models;
 
 namespace Presentation.AgentHub.Controllers;
 
 /// <summary>
-/// REST controller for agent resource management.
-/// Stub — conversation store integration added in section 03.
+/// Manages agent discovery and conversation history.
+/// All endpoints require authentication. Ownership is enforced at the conversation level:
+/// a user may only access or delete conversations where <see cref="ConversationRecord.UserId"/>
+/// matches their own identity claim.
 /// </summary>
 [ApiController]
-[Route("api/[controller]")]
+[Route("api")]
 [Authorize]
-public class AgentsController : ControllerBase
+public sealed class AgentsController : ControllerBase
 {
-    /// <summary>Returns the list of available agents. Requires authentication.</summary>
-    [HttpGet]
-    public IActionResult Get() => Ok(Array.Empty<object>());
+    private readonly IConversationStore _store;
+    private readonly AgentHubConfig _config;
+    private readonly ILogger<AgentsController> _logger;
+
+    /// <summary>Initialises the controller with its dependencies.</summary>
+    public AgentsController(
+        IConversationStore store,
+        IOptions<AgentHubConfig> config,
+        ILogger<AgentsController> logger)
+    {
+        _store = store;
+        _config = config.Value;
+        _logger = logger;
+    }
+
+    /// <summary>Returns the list of configured agents.</summary>
+    /// <remarks>
+    /// TODO: Enumerate the full agent registry once the agent framework exposes it.
+    /// For now returns a single summary derived from <see cref="AgentHubConfig.DefaultAgentName"/>.
+    /// </remarks>
+    [HttpGet("agents")]
+    public IActionResult GetAgents()
+    {
+        var agents = new[] { new AgentSummary(_config.DefaultAgentName, "Default agent") };
+        return Ok(agents);
+    }
+
+    /// <summary>Returns all conversations owned by the current user.</summary>
+    [HttpGet("conversations")]
+    public async Task<IActionResult> GetConversations(CancellationToken ct)
+    {
+        var userId = User.GetUserId();
+        var conversations = await _store.ListAsync(userId, ct);
+        return Ok(conversations);
+    }
+
+    /// <summary>Returns a single conversation. 404 if not found. 403 if not owned by caller.</summary>
+    [HttpGet("conversations/{id}")]
+    public async Task<IActionResult> GetConversation(string id, CancellationToken ct)
+    {
+        var record = await _store.GetAsync(id, ct);
+        if (record is null)
+            return NotFound();
+        if (record.UserId != User.GetUserId())
+        {
+            _logger.LogWarning("User {UserId} attempted to access conversation {ConversationId} owned by {OwnerId}.",
+                User.GetUserId(), id, record.UserId);
+            return Forbid();
+        }
+        return Ok(record);
+    }
+
+    /// <summary>Deletes a conversation. 403 if not owned by caller. 204 on success.</summary>
+    [HttpDelete("conversations/{id}")]
+    public async Task<IActionResult> DeleteConversation(string id, CancellationToken ct)
+    {
+        var record = await _store.GetAsync(id, ct);
+        if (record is null)
+            return NotFound();
+        if (record.UserId != User.GetUserId())
+        {
+            _logger.LogWarning("User {UserId} attempted to delete conversation {ConversationId} owned by {OwnerId}.",
+                User.GetUserId(), id, record.UserId);
+            return Forbid();
+        }
+
+        await _store.DeleteAsync(id, ct);
+        return NoContent();
+    }
 }
diff --git a/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs b/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
index 22ab23f..62e0c1f 100644
--- a/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
@@ -2,7 +2,9 @@ using Microsoft.AspNetCore.Authentication.JwtBearer;
 using Microsoft.AspNetCore.Http;
 using Microsoft.AspNetCore.RateLimiting;
 using Microsoft.Identity.Web;
+using Presentation.AgentHub.Interfaces;
 using Presentation.AgentHub.Models;
+using Presentation.AgentHub.Services;
 using System.Threading.RateLimiting;
 
 namespace Presentation.AgentHub;
@@ -109,8 +111,9 @@ public static class DependencyInjection
         services.Configure<AgentHubConfig>(
             configuration.GetSection("AppConfig:AgentHub"));
 
-        // Section 3 — FileSystemConversationStore
-        // services.AddSingleton<IConversationStore, FileSystemConversationStore>();
+        // Singleton: FileSystemConversationStore owns a SemaphoreSlim for thread-safety;
+        // a scoped/transient registration would create multiple semaphore instances.
+        services.AddSingleton<IConversationStore, FileSystemConversationStore>();
 
         // Section 5 — SignalRSpanExporter
         // services.AddSingleton<SignalRSpanExporter>();
diff --git a/src/Content/Presentation/Presentation.AgentHub/Extensions/ClaimsPrincipalExtensions.cs b/src/Content/Presentation/Presentation.AgentHub/Extensions/ClaimsPrincipalExtensions.cs
new file mode 100644
index 0000000..487b67a
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Extensions/ClaimsPrincipalExtensions.cs
@@ -0,0 +1,26 @@
+using System.Security.Claims;
+
+namespace Presentation.AgentHub.Extensions;
+
+/// <summary>Extension methods for <see cref="ClaimsPrincipal"/> to simplify Azure AD claim access.</summary>
+public static class ClaimsPrincipalExtensions
+{
+    /// <summary>
+    /// Returns the Azure AD object ID (OID) of the authenticated user.
+    /// Throws <see cref="InvalidOperationException"/> if the claim is absent —
+    /// this should never occur for endpoints protected by <c>[Authorize]</c> with
+    /// a valid Azure AD token.
+    /// </summary>
+    public static string GetUserId(this ClaimsPrincipal principal)
+    {
+        // Azure AD tokens include the object ID in either the standard "oid" claim
+        // or the namespaced "http://schemas.microsoft.com/identity/claims/objectidentifier" claim.
+        var oid = principal.FindFirstValue("oid")
+            ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
+
+        if (string.IsNullOrEmpty(oid))
+            throw new InvalidOperationException("The 'oid' claim is missing from the authenticated user's token.");
+
+        return oid;
+    }
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Interfaces/IConversationStore.cs b/src/Content/Presentation/Presentation.AgentHub/Interfaces/IConversationStore.cs
new file mode 100644
index 0000000..690a7e2
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Interfaces/IConversationStore.cs
@@ -0,0 +1,40 @@
+using Presentation.AgentHub.Models;
+
+namespace Presentation.AgentHub.Interfaces;
+
+/// <summary>
+/// Persistent store for conversation records. Thread-safe for concurrent access.
+/// Implementations must enforce user-ownership isolation — callers are responsible
+/// for checking <see cref="ConversationRecord.UserId"/> against the authenticated user
+/// before returning records to clients.
+/// </summary>
+public interface IConversationStore
+{
+    /// <summary>Returns the conversation with the given ID, or <c>null</c> if it does not exist.</summary>
+    Task<ConversationRecord?> GetAsync(string conversationId, CancellationToken ct = default);
+
+    /// <summary>
+    /// Returns all conversations owned by <paramref name="userId"/>.
+    /// O(n) in the number of stored conversations — acceptable for POC scale.
+    /// </summary>
+    Task<IReadOnlyList<ConversationRecord>> ListAsync(string userId, CancellationToken ct = default);
+
+    /// <summary>Creates a new conversation with a generated GUID id.</summary>
+    Task<ConversationRecord> CreateAsync(string agentName, string userId, CancellationToken ct = default);
+
+    /// <summary>Appends <paramref name="message"/> to an existing conversation record.</summary>
+    Task AppendMessageAsync(string conversationId, ConversationMessage message, CancellationToken ct = default);
+
+    /// <summary>Permanently deletes a conversation record.</summary>
+    Task DeleteAsync(string conversationId, CancellationToken ct = default);
+
+    /// <summary>
+    /// Returns the last <paramref name="maxMessages"/> messages from the conversation,
+    /// or <c>null</c> if the conversation does not exist.
+    /// Called by the hub before dispatching to the agent to prevent unbounded token growth.
+    /// </summary>
+    Task<IReadOnlyList<ConversationMessage>?> GetHistoryForDispatch(
+        string conversationId,
+        int maxMessages,
+        CancellationToken ct = default);
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/AgentSummary.cs b/src/Content/Presentation/Presentation.AgentHub/Models/AgentSummary.cs
new file mode 100644
index 0000000..f9dafba
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/AgentSummary.cs
@@ -0,0 +1,4 @@
+namespace Presentation.AgentHub.Models;
+
+/// <summary>Lightweight DTO returned by GET /api/agents.</summary>
+public sealed record AgentSummary(string Name, string Description);
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/ConversationMessage.cs b/src/Content/Presentation/Presentation.AgentHub/Models/ConversationMessage.cs
new file mode 100644
index 0000000..1e518cc
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/ConversationMessage.cs
@@ -0,0 +1,8 @@
+namespace Presentation.AgentHub.Models;
+
+/// <summary>A single message in a conversation. Role determines rendering behavior in the UI.</summary>
+public sealed record ConversationMessage(
+    MessageRole Role,
+    string Content,
+    DateTimeOffset Timestamp,
+    IReadOnlyList<ToolCallRecord>? ToolCalls = null);
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/ConversationRecord.cs b/src/Content/Presentation/Presentation.AgentHub/Models/ConversationRecord.cs
new file mode 100644
index 0000000..8c3ee94
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/ConversationRecord.cs
@@ -0,0 +1,13 @@
+namespace Presentation.AgentHub.Models;
+
+/// <summary>
+/// Full conversation state persisted to disk. UserId is the object ID (OID claim)
+/// of the owning Azure AD user. Never expose records to users other than the owner.
+/// </summary>
+public sealed record ConversationRecord(
+    string Id,
+    string AgentName,
+    string UserId,
+    DateTimeOffset CreatedAt,
+    DateTimeOffset UpdatedAt,
+    IReadOnlyList<ConversationMessage> Messages);
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/MessageRole.cs b/src/Content/Presentation/Presentation.AgentHub/Models/MessageRole.cs
new file mode 100644
index 0000000..2705a21
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/MessageRole.cs
@@ -0,0 +1,17 @@
+namespace Presentation.AgentHub.Models;
+
+/// <summary>Identifies the author or purpose of a conversation message.</summary>
+public enum MessageRole
+{
+    /// <summary>A message authored by the end user.</summary>
+    User,
+
+    /// <summary>A message generated by the AI assistant.</summary>
+    Assistant,
+
+    /// <summary>A system-level instruction injected before the conversation begins.</summary>
+    System,
+
+    /// <summary>The result of a tool invocation, fed back to the assistant.</summary>
+    Tool,
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/ToolCallRecord.cs b/src/Content/Presentation/Presentation.AgentHub/Models/ToolCallRecord.cs
new file mode 100644
index 0000000..b6cb21b
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/ToolCallRecord.cs
@@ -0,0 +1,10 @@
+using System.Text.Json;
+
+namespace Presentation.AgentHub.Models;
+
+/// <summary>Captures a single tool invocation within an assistant turn.</summary>
+public sealed record ToolCallRecord(
+    string ToolName,
+    JsonElement Input,
+    JsonElement Output,
+    long DurationMs);
diff --git a/src/Content/Presentation/Presentation.AgentHub/Services/FileSystemConversationStore.cs b/src/Content/Presentation/Presentation.AgentHub/Services/FileSystemConversationStore.cs
new file mode 100644
index 0000000..250002b
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Services/FileSystemConversationStore.cs
@@ -0,0 +1,231 @@
+using System.Text.Json;
+using System.Text.Json.Serialization;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+using Presentation.AgentHub.Interfaces;
+using Presentation.AgentHub.Models;
+
+namespace Presentation.AgentHub.Services;
+
+/// <summary>
+/// File-system-backed conversation store. Each <see cref="ConversationRecord"/> is stored as a
+/// JSON file at <c>{ConversationsPath}/{conversationId}.json</c>.
+///
+/// Thread safety: a single <see cref="SemaphoreSlim"/> serializes all file I/O. This is
+/// intentionally simple for POC scale. A production implementation should use
+/// per-conversation-id locking (e.g., AsyncKeyedLock) to allow concurrent operations
+/// across different conversations.
+///
+/// Atomic writes: all writes go to a <c>.tmp</c> file first, then <see cref="File.Move"/> with
+/// <c>overwrite: true</c>. This prevents partial-write corruption if the process exits mid-write.
+///
+/// Path safety: the constructor resolves <c>ConversationsPath</c> to an absolute path.
+/// Any operation whose computed file path does not start with this base path throws
+/// <see cref="ArgumentException"/>, preventing path-traversal attacks via crafted conversation IDs.
+/// </summary>
+public sealed class FileSystemConversationStore : IConversationStore
+{
+    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
+    {
+        Converters = { new JsonStringEnumConverter() },
+        WriteIndented = false,
+    };
+
+    private readonly string _basePath;
+    private readonly SemaphoreSlim _lock = new(1, 1);
+    private readonly ILogger<FileSystemConversationStore> _logger;
+
+    /// <summary>
+    /// Initialises the store, resolving <see cref="AgentHubConfig.ConversationsPath"/> to an
+    /// absolute path and creating the directory if it does not yet exist.
+    /// </summary>
+    public FileSystemConversationStore(
+        IOptions<AgentHubConfig> config,
+        ILogger<FileSystemConversationStore> logger)
+    {
+        _basePath = Path.GetFullPath(config.Value.ConversationsPath);
+        _logger = logger;
+        Directory.CreateDirectory(_basePath);
+    }
+
+    /// <inheritdoc/>
+    public async Task<ConversationRecord?> GetAsync(string conversationId, CancellationToken ct = default)
+    {
+        var path = ResolveAndValidatePath(conversationId);
+
+        await _lock.WaitAsync(ct);
+        try
+        {
+            if (!File.Exists(path))
+                return null;
+
+            var json = await File.ReadAllTextAsync(path, ct);
+            return JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions);
+        }
+        finally
+        {
+            _lock.Release();
+        }
+    }
+
+    /// <inheritdoc/>
+    public async Task<IReadOnlyList<ConversationRecord>> ListAsync(string userId, CancellationToken ct = default)
+    {
+        await _lock.WaitAsync(ct);
+        try
+        {
+            var files = Directory.GetFiles(_basePath, "*.json");
+            var results = new List<ConversationRecord>();
+
+            foreach (var file in files)
+            {
+                ct.ThrowIfCancellationRequested();
+                try
+                {
+                    var json = await File.ReadAllTextAsync(file, ct);
+                    var record = JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions);
+                    if (record?.UserId == userId)
+                        results.Add(record);
+                }
+                catch (Exception ex)
+                {
+                    _logger.LogWarning(ex, "Failed to deserialize conversation file {File}; skipping.", file);
+                }
+            }
+
+            return results;
+        }
+        finally
+        {
+            _lock.Release();
+        }
+    }
+
+    /// <inheritdoc/>
+    public async Task<ConversationRecord> CreateAsync(string agentName, string userId, CancellationToken ct = default)
+    {
+        var id = Guid.NewGuid().ToString();
+        var now = DateTimeOffset.UtcNow;
+        var record = new ConversationRecord(
+            Id: id,
+            AgentName: agentName,
+            UserId: userId,
+            CreatedAt: now,
+            UpdatedAt: now,
+            Messages: []);
+
+        var path = ResolveAndValidatePath(id);
+        await WriteAtomicAsync(path, record, ct);
+
+        _logger.LogDebug("Created conversation {ConversationId} for user {UserId}.", id, userId);
+        return record;
+    }
+
+    /// <inheritdoc/>
+    public async Task AppendMessageAsync(string conversationId, ConversationMessage message, CancellationToken ct = default)
+    {
+        var path = ResolveAndValidatePath(conversationId);
+
+        await _lock.WaitAsync(ct);
+        try
+        {
+            var json = await File.ReadAllTextAsync(path, ct);
+            var existing = JsonSerializer.Deserialize<ConversationRecord>(json, _jsonOptions)
+                ?? throw new InvalidOperationException($"Conversation {conversationId} could not be deserialized.");
+
+            var updated = existing with
+            {
+                Messages = [..existing.Messages, message],
+                UpdatedAt = DateTimeOffset.UtcNow,
+            };
+
+            await WriteAtomicLockedAsync(path, updated, ct);
+        }
+        finally
+        {
+            _lock.Release();
+        }
+    }
+
+    /// <inheritdoc/>
+    public async Task DeleteAsync(string conversationId, CancellationToken ct = default)
+    {
+        var path = ResolveAndValidatePath(conversationId);
+
+        await _lock.WaitAsync(ct);
+        try
+        {
+            if (File.Exists(path))
+                File.Delete(path);
+        }
+        finally
+        {
+            _lock.Release();
+        }
+    }
+
+    /// <inheritdoc/>
+    public async Task<IReadOnlyList<ConversationMessage>?> GetHistoryForDispatch(
+        string conversationId,
+        int maxMessages,
+        CancellationToken ct = default)
+    {
+        var record = await GetAsync(conversationId, ct);
+        if (record is null)
+            return null;
+
+        var messages = record.Messages;
+        if (messages.Count <= maxMessages)
+            return messages;
+
+        return messages.Skip(messages.Count - maxMessages).ToList();
+    }
+
+    // -------------------------------------------------------------------------
+    // Private helpers
+    // -------------------------------------------------------------------------
+
+    private string ResolveAndValidatePath(string conversationId)
+    {
+        // Resolve the full path and verify it stays within _basePath to prevent
+        // path-traversal attacks via crafted conversation IDs like "../evil".
+        var fullPath = Path.GetFullPath(Path.Combine(_basePath, $"{conversationId}.json"));
+        if (!fullPath.StartsWith(_basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
+            && !fullPath.Equals(_basePath, StringComparison.OrdinalIgnoreCase))
+        {
+            throw new ArgumentException(
+                $"Conversation ID '{conversationId}' resolves outside the allowed base path.",
+                nameof(conversationId));
+        }
+        return fullPath;
+    }
+
+    /// <summary>
+    /// Writes <paramref name="record"/> atomically (tmp → move) while holding <see cref="_lock"/>.
+    /// Call this only from <see cref="CreateAsync"/> where the lock is not yet held.
+    /// </summary>
+    private async Task WriteAtomicAsync(string targetPath, ConversationRecord record, CancellationToken ct)
+    {
+        await _lock.WaitAsync(ct);
+        try
+        {
+            await WriteAtomicLockedAsync(targetPath, record, ct);
+        }
+        finally
+        {
+            _lock.Release();
+        }
+    }
+
+    /// <summary>
+    /// Writes <paramref name="record"/> atomically (tmp → move). Must be called while the
+    /// caller already holds <see cref="_lock"/>.
+    /// </summary>
+    private static async Task WriteAtomicLockedAsync(string targetPath, ConversationRecord record, CancellationToken ct)
+    {
+        var tmpPath = targetPath + ".tmp";
+        var json = JsonSerializer.Serialize(record, _jsonOptions);
+        await File.WriteAllTextAsync(tmpPath, json, ct);
+        File.Move(tmpPath, targetPath, overwrite: true);
+    }
+}
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/AgentsControllerTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/AgentsControllerTests.cs
new file mode 100644
index 0000000..1d4edf5
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/AgentsControllerTests.cs
@@ -0,0 +1,37 @@
+using Xunit;
+
+namespace Presentation.AgentHub.Tests.Controllers;
+
+/// <summary>
+/// Integration tests for AgentsController ownership enforcement.
+/// Stubs — wired up fully in section-07 once TestAuthHandler supports per-test claim injection.
+/// </summary>
+public sealed class AgentsControllerTests : IClassFixture<TestWebApplicationFactory>
+{
+    public AgentsControllerTests(TestWebApplicationFactory factory) { }
+
+    [Fact]
+    public async Task GetConversations_ReturnsOnlyConversationsOwnedByAuthenticatedUser()
+    {
+        // Implemented in section-07 with per-test user identity override.
+        await Task.CompletedTask;
+    }
+
+    [Fact]
+    public async Task GetConversationById_AnotherUsersConversation_Returns403()
+    {
+        await Task.CompletedTask;
+    }
+
+    [Fact]
+    public async Task DeleteConversation_AnotherUsersConversation_Returns403()
+    {
+        await Task.CompletedTask;
+    }
+
+    [Fact]
+    public async Task DeleteConversation_OwnConversation_Returns204AndRemovesFile()
+    {
+        await Task.CompletedTask;
+    }
+}
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/ConversationStore/FileSystemConversationStoreTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/ConversationStore/FileSystemConversationStoreTests.cs
new file mode 100644
index 0000000..c8c4e33
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/ConversationStore/FileSystemConversationStoreTests.cs
@@ -0,0 +1,175 @@
+using Microsoft.Extensions.Logging.Abstractions;
+using Xunit;
+using Microsoft.Extensions.Options;
+using Presentation.AgentHub.Models;
+using Presentation.AgentHub.Services;
+
+namespace Presentation.AgentHub.Tests.ConversationStore;
+
+/// <summary>Unit tests for <see cref="FileSystemConversationStore"/> using a temp directory fixture.</summary>
+public sealed class FileSystemConversationStoreTests : IDisposable
+{
+    private readonly string _tempDir;
+    private readonly FileSystemConversationStore _store;
+
+    public FileSystemConversationStoreTests()
+    {
+        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
+        Directory.CreateDirectory(_tempDir);
+
+        var config = Options.Create(new AgentHubConfig
+        {
+            ConversationsPath = _tempDir,
+            DefaultAgentName = "test-agent",
+            MaxHistoryMessages = 20,
+        });
+
+        _store = new FileSystemConversationStore(config, NullLogger<FileSystemConversationStore>.Instance);
+    }
+
+    public void Dispose()
+    {
+        if (Directory.Exists(_tempDir))
+            Directory.Delete(_tempDir, recursive: true);
+    }
+
+    [Fact]
+    public async Task CreateAsync_WritesJsonFileAtExpectedPath()
+    {
+        var record = await _store.CreateAsync("agent", "user1");
+
+        var expectedPath = Path.Combine(_tempDir, $"{record.Id}.json");
+        Assert.True(File.Exists(expectedPath));
+    }
+
+    [Fact]
+    public async Task GetAsync_ReturnsNullForUnknownConversationId()
+    {
+        var result = await _store.GetAsync(Guid.NewGuid().ToString());
+
+        Assert.Null(result);
+    }
+
+    [Fact]
+    public async Task GetAsync_DeserializesConversationRecordCorrectly()
+    {
+        var created = await _store.CreateAsync("my-agent", "user-abc");
+
+        var retrieved = await _store.GetAsync(created.Id);
+
+        Assert.NotNull(retrieved);
+        Assert.Equal(created.Id, retrieved.Id);
+        Assert.Equal("my-agent", retrieved.AgentName);
+        Assert.Equal("user-abc", retrieved.UserId);
+        Assert.Empty(retrieved.Messages);
+    }
+
+    [Fact]
+    public async Task AppendMessageAsync_UpdatesFileAtomically()
+    {
+        var record = await _store.CreateAsync("agent", "user1");
+        var message = new ConversationMessage(MessageRole.User, "hello", DateTimeOffset.UtcNow);
+
+        await _store.AppendMessageAsync(record.Id, message);
+
+        var filePath = Path.Combine(_tempDir, $"{record.Id}.json");
+        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
+
+        Assert.True(File.Exists(filePath));
+        Assert.Empty(tmpFiles);
+
+        var updated = await _store.GetAsync(record.Id);
+        Assert.NotNull(updated);
+        Assert.Single(updated.Messages);
+        Assert.Equal("hello", updated.Messages[0].Content);
+    }
+
+    [Fact]
+    public async Task DeleteAsync_RemovesTheFile()
+    {
+        var record = await _store.CreateAsync("agent", "user1");
+        var filePath = Path.Combine(_tempDir, $"{record.Id}.json");
+        Assert.True(File.Exists(filePath));
+
+        await _store.DeleteAsync(record.Id);
+
+        Assert.False(File.Exists(filePath));
+    }
+
+    [Fact]
+    public async Task ListAsync_ReturnsOnlyConversationsBelongingToGivenUserId()
+    {
+        await _store.CreateAsync("agent", "user-a");
+        await _store.CreateAsync("agent", "user-a");
+        await _store.CreateAsync("agent", "user-b");
+
+        var userAConversations = await _store.ListAsync("user-a");
+        var userBConversations = await _store.ListAsync("user-b");
+
+        Assert.Equal(2, userAConversations.Count);
+        Assert.Single(userBConversations);
+        Assert.All(userAConversations, c => Assert.Equal("user-a", c.UserId));
+        Assert.All(userBConversations, c => Assert.Equal("user-b", c.UserId));
+    }
+
+    [Fact]
+    public async Task ConcurrentAppendMessageAsync_OnSameConversationId_DoesNotCorruptFile()
+    {
+        const int messageCount = 20;
+        var record = await _store.CreateAsync("agent", "user1");
+
+        var tasks = Enumerable.Range(0, messageCount)
+            .Select(i => _store.AppendMessageAsync(
+                record.Id,
+                new ConversationMessage(MessageRole.User, $"message-{i}", DateTimeOffset.UtcNow)));
+
+        await Task.WhenAll(tasks);
+
+        var updated = await _store.GetAsync(record.Id);
+        Assert.NotNull(updated);
+        Assert.Equal(messageCount, updated.Messages.Count);
+    }
+
+    [Fact]
+    public async Task PathWithTraversalCharacters_ThrowsArgumentException()
+    {
+        await Assert.ThrowsAsync<ArgumentException>(() =>
+            _store.GetAsync("../evil"));
+
+        await Assert.ThrowsAsync<ArgumentException>(() =>
+            _store.DeleteAsync("../../etc/passwd"));
+    }
+
+    [Fact]
+    public async Task GetHistoryForDispatch_ReturnsAtMostMaxMessages()
+    {
+        var record = await _store.CreateAsync("agent", "user1");
+
+        for (var i = 0; i < 15; i++)
+            await _store.AppendMessageAsync(record.Id,
+                new ConversationMessage(MessageRole.User, $"msg-{i}", DateTimeOffset.UtcNow));
+
+        var history = await _store.GetHistoryForDispatch(record.Id, maxMessages: 5);
+
+        Assert.NotNull(history);
+        Assert.Equal(5, history.Count);
+    }
+
+    [Fact]
+    public async Task GetHistoryForDispatch_ReturnsLastNMessages_NotFirstN()
+    {
+        var record = await _store.CreateAsync("agent", "user1");
+
+        for (var i = 0; i < 30; i++)
+            await _store.AppendMessageAsync(record.Id,
+                new ConversationMessage(MessageRole.User, $"msg-{i}", DateTimeOffset.UtcNow));
+
+        var history = await _store.GetHistoryForDispatch(record.Id, maxMessages: 10);
+
+        Assert.NotNull(history);
+        Assert.Equal(10, history.Count);
+        // The last 10 messages should be msg-20 through msg-29
+        Assert.Equal("msg-20", history[0].Content);
+        Assert.Equal("msg-29", history[9].Content);
+    }
+}
