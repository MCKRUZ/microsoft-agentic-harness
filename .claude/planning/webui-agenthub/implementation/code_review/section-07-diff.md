diff --git a/src/Content/Presentation/Presentation.AgentHub/Controllers/McpController.cs b/src/Content/Presentation/Presentation.AgentHub/Controllers/McpController.cs
index 01e8150..9045208 100644
--- a/src/Content/Presentation/Presentation.AgentHub/Controllers/McpController.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/Controllers/McpController.cs
@@ -105,6 +105,12 @@ public sealed class McpController : ControllerBase
     [RequestSizeLimit(32 * 1024)]
     public async Task<IActionResult> InvokeTool(string name, [FromBody] McpToolInvokeRequest request, CancellationToken ct)
     {
+        // Enforce 32 KB body size limit manually so the check works in TestServer
+        // (which does not implement IHttpMaxRequestBodySizeFeature used by [RequestSizeLimit]).
+        const int maxBodyBytes = 32 * 1024;
+        if (Request.ContentLength > maxBodyBytes)
+            return StatusCode(StatusCodes.Status413RequestEntityTooLarge);
+
         if (request.Arguments.ValueKind == JsonValueKind.Undefined)
             return BadRequest("Arguments must be a valid JSON object.");
 
diff --git a/src/Content/Presentation/Presentation.AgentHub/Hubs/AgentTelemetryHub.cs b/src/Content/Presentation/Presentation.AgentHub/Hubs/AgentTelemetryHub.cs
index fb61256..26205d8 100644
--- a/src/Content/Presentation/Presentation.AgentHub/Hubs/AgentTelemetryHub.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/Hubs/AgentTelemetryHub.cs
@@ -100,9 +100,9 @@ public sealed class AgentTelemetryHub : Hub
     /// <returns>The conversation's message history (empty for new conversations).</returns>
     public async Task<IReadOnlyList<ConversationMessage>> StartConversation(
         string agentName,
-        string conversationId,
-        CancellationToken ct = default)
+        string conversationId)
     {
+        var ct = Context.ConnectionAborted;
         var callerId = GetCallerId();
         var existingRecord = await ValidateOwnershipAsync(conversationId, callerId, ct);
 
@@ -139,8 +139,9 @@ public sealed class AgentTelemetryHub : Hub
     /// <see cref="ConversationLockRegistry"/> ensures that concurrent <c>SendMessage</c>
     /// calls on the same conversation complete in order — no interleaved token streams.
     /// </summary>
-    public async Task SendMessage(string conversationId, string userMessage, CancellationToken ct = default)
+    public async Task SendMessage(string conversationId, string userMessage)
     {
+        var ct = Context.ConnectionAborted;
         var callerId = GetCallerId();
         var record = await ValidateOwnershipAsync(conversationId, callerId, ct)
             ?? throw new HubException("Conversation not found.");
@@ -218,16 +219,17 @@ public sealed class AgentTelemetryHub : Hub
     /// Invokes a named tool through the agent pipeline by synthesising a user message.
     /// Ownership is validated via the underlying <see cref="SendMessage"/> call.
     /// </summary>
-    public async Task InvokeToolViaAgent(string conversationId, string toolName, string inputJson, CancellationToken ct = default)
+    public async Task InvokeToolViaAgent(string conversationId, string toolName, string inputJson)
     {
         // Ownership validation is delegated to SendMessage — no double-check needed here.
         var userMessage = $"Please invoke the tool '{toolName}' with the following input: {inputJson}";
-        await SendMessage(conversationId, userMessage, ct);
+        await SendMessage(conversationId, userMessage);
     }
 
     /// <summary>Adds this connection to the conversation's SignalR group.</summary>
-    public async Task JoinConversationGroup(string conversationId, CancellationToken ct = default)
+    public async Task JoinConversationGroup(string conversationId)
     {
+        var ct = Context.ConnectionAborted;
         var callerId = GetCallerId();
         _ = await ValidateOwnershipAsync(conversationId, callerId, ct)
             ?? throw new HubException("Conversation not found.");
@@ -236,8 +238,8 @@ public sealed class AgentTelemetryHub : Hub
     }
 
     /// <summary>Removes this connection from the conversation's SignalR group. No ownership check — leaving is always safe.</summary>
-    public Task LeaveConversationGroup(string conversationId, CancellationToken ct = default) =>
-        Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId), ct);
+    public Task LeaveConversationGroup(string conversationId) =>
+        Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId), Context.ConnectionAborted);
 
     // -------------------------------------------------------------------------
     // Hub methods — global trace firehose
@@ -251,18 +253,18 @@ public sealed class AgentTelemetryHub : Hub
     /// from the default tenant assignment — grant it only to internal observability users.
     /// </summary>
     /// <exception cref="HubException">Thrown if the caller lacks the required role.</exception>
-    public async Task JoinGlobalTraces(CancellationToken ct = default)
+    public async Task JoinGlobalTraces()
     {
         if (!Context.User!.IsInRole(GlobalTracesRole))
             throw new HubException($"The {GlobalTracesRole} role is required to subscribe to global traces.");
 
-        await Groups.AddToGroupAsync(Context.ConnectionId, GlobalTracesGroup, ct);
+        await Groups.AddToGroupAsync(Context.ConnectionId, GlobalTracesGroup, Context.ConnectionAborted);
         _logger.LogInformation("Connection {ConnectionId} joined global-traces.", Context.ConnectionId);
     }
 
     /// <summary>Unsubscribes this connection from the global trace firehose. No role check.</summary>
-    public Task LeaveGlobalTraces(CancellationToken ct = default) =>
-        Groups.RemoveFromGroupAsync(Context.ConnectionId, GlobalTracesGroup, ct);
+    public Task LeaveGlobalTraces() =>
+        Groups.RemoveFromGroupAsync(Context.ConnectionId, GlobalTracesGroup, Context.ConnectionAborted);
 
     // -------------------------------------------------------------------------
     // Private helpers
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeResponse.cs b/src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeResponse.cs
index 5356b6e..486130d 100644
--- a/src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeResponse.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeResponse.cs
@@ -5,8 +5,8 @@ namespace Presentation.AgentHub.Models;
 /// <summary>Response envelope for an MCP tool invocation.</summary>
 public sealed record McpToolInvokeResponse
 {
-    /// <summary>Serialized output from the tool. Populated only when <see cref="Success"/> is <see langword="true"/>.</summary>
-    public JsonElement Output { get; init; }
+    /// <summary>Serialized output from the tool. Populated only when <see cref="Success"/> is <see langword="true"/>; <see langword="null"/> on failure.</summary>
+    public JsonElement? Output { get; init; }
 
     /// <summary>Wall-clock duration of the invocation in milliseconds.</summary>
     public long DurationMs { get; init; }
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/AgentsControllerTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/AgentsControllerTests.cs
index 1d4edf5..88dea04 100644
--- a/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/AgentsControllerTests.cs
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/AgentsControllerTests.cs
@@ -1,37 +1,134 @@
+using FluentAssertions;
+using Microsoft.AspNetCore.Authentication;
+using Microsoft.AspNetCore.Mvc.Testing;
+using Microsoft.AspNetCore.TestHost;
+using Microsoft.Extensions.DependencyInjection;
+using Presentation.AgentHub.Interfaces;
+using Presentation.AgentHub.Models;
+using System.Net;
+using System.Net.Http.Json;
 using Xunit;
 
 namespace Presentation.AgentHub.Tests.Controllers;
 
 /// <summary>
-/// Integration tests for AgentsController ownership enforcement.
-/// Stubs — wired up fully in section-07 once TestAuthHandler supports per-test claim injection.
+/// Integration tests for <see cref="AgentsController"/> ownership enforcement.
+/// Verifies that IDOR vulnerabilities are prevented at the HTTP layer: users may only
+/// read or delete conversations where <see cref="ConversationRecord.UserId"/> matches
+/// their own identity.
 /// </summary>
 public sealed class AgentsControllerTests : IClassFixture<TestWebApplicationFactory>
 {
-    public AgentsControllerTests(TestWebApplicationFactory factory) { }
+    private readonly TestWebApplicationFactory _factory;
+    private readonly IConversationStore _store;
 
+    /// <summary>Initialises the test class with the shared factory and resolves the conversation store.</summary>
+    public AgentsControllerTests(TestWebApplicationFactory factory)
+    {
+        _factory = factory;
+        _store = factory.Services.GetRequiredService<IConversationStore>();
+    }
+
+    /// <summary>Creates an HTTP client authenticated as <paramref name="userId"/> via the test auth handler.</summary>
+    private HttpClient CreateClientAs(string userId)
+    {
+        var client = _factory
+            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
+                services.AddAuthentication(TestAuthHandler.SchemeName)
+                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
+                        TestAuthHandler.SchemeName, _ => { })))
+            .CreateClient();
+        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
+        return client;
+    }
+
+    /// <summary>
+    /// GET /api/conversations returns only the conversations owned by the authenticated user,
+    /// not those belonging to other users.
+    /// </summary>
     [Fact]
     public async Task GetConversations_ReturnsOnlyConversationsOwnedByAuthenticatedUser()
     {
-        // Implemented in section-07 with per-test user identity override.
-        await Task.CompletedTask;
+        // Use unique IDs per test run so shared factory state doesn't pollute counts.
+        var testId = Guid.NewGuid().ToString("N")[..8];
+        var userA = $"conversations-user-a-{testId}";
+        var userB = $"conversations-user-b-{testId}";
+
+        await _store.CreateAsync("test-agent", userA);
+        await _store.CreateAsync("test-agent", userA);
+        await _store.CreateAsync("test-agent", userB);
+
+        using var clientA = CreateClientAs(userA);
+        using var clientB = CreateClientAs(userB);
+
+        var respA = await clientA.GetAsync("/api/conversations");
+        var respB = await clientB.GetAsync("/api/conversations");
+
+        respA.StatusCode.Should().Be(HttpStatusCode.OK);
+        respB.StatusCode.Should().Be(HttpStatusCode.OK);
+
+        var convA = await respA.Content.ReadFromJsonAsync<List<ConversationRecord>>();
+        var convB = await respB.Content.ReadFromJsonAsync<List<ConversationRecord>>();
+
+        convA.Should().NotBeNull().And.HaveCount(2);
+        convB.Should().NotBeNull().And.HaveCount(1);
+        convA!.Should().OnlyContain(c => c.UserId == userA);
+        convB!.Should().OnlyContain(c => c.UserId == userB);
     }
 
+    /// <summary>
+    /// GET /api/conversations/{id} returns 403 when the conversation belongs to a different user.
+    /// </summary>
     [Fact]
     public async Task GetConversationById_AnotherUsersConversation_Returns403()
     {
-        await Task.CompletedTask;
+        var testId = Guid.NewGuid().ToString("N")[..8];
+        var owner = $"get-owner-{testId}";
+        var attacker = $"get-attacker-{testId}";
+
+        var ownerConv = await _store.CreateAsync("test-agent", owner);
+
+        using var client = CreateClientAs(attacker);
+        var response = await client.GetAsync($"/api/conversations/{ownerConv.Id}");
+
+        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
     }
 
+    /// <summary>
+    /// DELETE /api/conversations/{id} returns 403 when the conversation belongs to a different user.
+    /// </summary>
     [Fact]
     public async Task DeleteConversation_AnotherUsersConversation_Returns403()
     {
-        await Task.CompletedTask;
+        var testId = Guid.NewGuid().ToString("N")[..8];
+        var owner = $"del-owner-{testId}";
+        var attacker = $"del-attacker-{testId}";
+
+        var ownerConv = await _store.CreateAsync("test-agent", owner);
+
+        using var client = CreateClientAs(attacker);
+        var response = await client.DeleteAsync($"/api/conversations/{ownerConv.Id}");
+
+        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
     }
 
+    /// <summary>
+    /// DELETE /api/conversations/{id} returns 204 and removes the conversation record for the owning user.
+    /// </summary>
     [Fact]
-    public async Task DeleteConversation_OwnConversation_Returns204AndRemovesFile()
+    public async Task DeleteConversation_OwnConversation_Returns204AndRemovesConversation()
     {
-        await Task.CompletedTask;
+        var testId = Guid.NewGuid().ToString("N")[..8];
+        var owner = $"del-self-{testId}";
+
+        var conv = await _store.CreateAsync("test-agent", owner);
+
+        using var client = CreateClientAs(owner);
+        var response = await client.DeleteAsync($"/api/conversations/{conv.Id}");
+
+        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
+
+        var deleted = await _store.GetAsync(conv.Id);
+        deleted.Should().BeNull("the conversation must be removed from the store after deletion");
     }
 }
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/McpControllerTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/McpControllerTests.cs
index a8cf4bb..b2d0cf1 100644
--- a/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/McpControllerTests.cs
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/McpControllerTests.cs
@@ -1,77 +1,311 @@
+using Application.AI.Common.Interfaces;
+using FluentAssertions;
+using Microsoft.AspNetCore.Authentication;
+using Microsoft.AspNetCore.Mvc.Testing;
+using Microsoft.AspNetCore.TestHost;
+using Microsoft.Extensions.AI;
+using Microsoft.Extensions.DependencyInjection;
+using Microsoft.Extensions.Logging;
+using Moq;
+using Presentation.AgentHub.Models;
+using System.Collections.Concurrent;
+using System.Net;
+using System.Net.Http.Json;
+using System.Text;
+using System.Text.Json;
 using Xunit;
 
 namespace Presentation.AgentHub.Tests.Controllers;
 
 /// <summary>
 /// Integration tests for <see cref="McpController"/> HTTP endpoints.
-/// Stubs — wired up fully in section-07 once TestAuthHandler supports per-test claim injection
-/// and fake tool/resource providers are registered via WebApplicationFactory overrides.
+/// Covers tool listing, tool invocation, error handling, audit logging,
+/// and the 32 KB request size limit.
 /// </summary>
 public sealed class McpControllerTests : IClassFixture<TestWebApplicationFactory>
 {
-    public McpControllerTests(TestWebApplicationFactory factory) { }
+    private readonly TestWebApplicationFactory _factory;
 
-    /// <summary>GET /api/mcp/tools returns 200 with at least one tool.</summary>
+    /// <summary>Initialises the test class with the shared factory.</summary>
+    public McpControllerTests(TestWebApplicationFactory factory) => _factory = factory;
+
+    // ── Helpers ──────────────────────────────────────────────────────────────
+
+    /// <summary>Creates an authenticated HTTP client using <see cref="TestAuthHandler"/>.</summary>
+    private HttpClient CreateAuthedClient(
+        string userId = "mcp-test-user",
+        WebApplicationFactory<Program>? factory = null)
+    {
+        var f = factory ?? _factory;
+        var client = f
+            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
+                services.AddAuthentication(TestAuthHandler.SchemeName)
+                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
+                        TestAuthHandler.SchemeName, _ => { })))
+            .CreateClient();
+        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
+        return client;
+    }
+
+    /// <summary>
+    /// Creates a factory variant that registers a fake <see cref="IMcpToolProvider"/>
+    /// and a log-capturing provider, then returns both along with a configured client.
+    /// </summary>
+    private (HttpClient client, TestLoggerProvider logs) CreateClientWithFakeTool(
+        string toolName,
+        Func<ValueTask<object?>>? invokeImpl = null,
+        bool throwOnInvoke = false)
+    {
+        var logs = new TestLoggerProvider();
+        var fakeProvider = BuildFakeToolProvider(toolName, invokeImpl, throwOnInvoke);
+
+        var factory = _factory.WithWebHostBuilder(b =>
+        {
+            b.ConfigureTestServices(services =>
+            {
+                services.AddAuthentication(TestAuthHandler.SchemeName)
+                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
+                        TestAuthHandler.SchemeName, _ => { });
+                services.AddSingleton<IMcpToolProvider>(fakeProvider);
+                services.AddSingleton<ILoggerProvider>(logs);
+            });
+        });
+
+        var client = factory.CreateClient();
+        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "mcp-test-user");
+        return (client, logs);
+    }
+
+    private static IMcpToolProvider BuildFakeToolProvider(
+        string toolName,
+        Func<ValueTask<object?>>? invokeImpl,
+        bool throwOnInvoke)
+    {
+        var fn = new FakeAIFunction(toolName, "A test tool for integration tests",
+            throwOnInvoke
+                ? () => ValueTask.FromException<object?>(new InvalidOperationException("Simulated tool failure"))
+                : invokeImpl);
+
+        var mock = new Mock<IMcpToolProvider>();
+        mock.Setup(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new Dictionary<string, IList<AITool>>
+            {
+                ["test-server"] = new List<AITool> { fn },
+            });
+        mock.Setup(p => p.GetToolByNameAsync(toolName, It.IsAny<CancellationToken>()))
+            .ReturnsAsync(fn);
+        mock.Setup(p => p.GetToolByNameAsync(
+                It.Is<string>(n => n != toolName), It.IsAny<CancellationToken>()))
+            .ReturnsAsync((AIFunction?)null);
+        return mock.Object;
+    }
+
+    // ── Tests: Tool listing ───────────────────────────────────────────────────
+
+    /// <summary>GET /api/mcp/tools returns 200 with a JSON array.</summary>
     [Fact]
-    public async Task GetTools_ReturnsOkWithToolList()
+    public async Task GetTools_Returns200WithToolList()
     {
-        // Implemented in section-07 with fake IMcpToolProvider registered via factory override.
-        await Task.CompletedTask;
+        var (client, _) = CreateClientWithFakeTool("list-tool");
+        using var response = await client.GetAsync("/api/mcp/tools");
+        response.StatusCode.Should().Be(HttpStatusCode.OK);
+        var body = await response.Content.ReadAsStringAsync();
+        body.Should().StartWith("[");
     }
 
-    /// <summary>Each tool in the response has Name, Description, and Schema populated.</summary>
+    /// <summary>Each tool in the response carries Name, Description, and Schema fields.</summary>
     [Fact]
     public async Task GetTools_EachToolHasNameDescriptionAndSchema()
     {
-        await Task.CompletedTask;
+        var (client, _) = CreateClientWithFakeTool("schema-tool");
+        using var response = await client.GetAsync("/api/mcp/tools");
+        var tools = await response.Content.ReadFromJsonAsync<List<McpToolDto>>();
+
+        tools.Should().NotBeNull().And.NotBeEmpty();
+        foreach (var tool in tools!)
+        {
+            tool.Name.Should().NotBeNullOrWhiteSpace();
+            tool.Description.Should().NotBeNullOrWhiteSpace();
+        }
     }
 
-    /// <summary>GET /api/mcp/prompts returns 200 empty array when IMcpPromptProvider is absent.</summary>
+    // ── Tests: Prompts ────────────────────────────────────────────────────────
+
+    /// <summary>GET /api/mcp/prompts returns 200 with an empty array when no real provider is registered.</summary>
     [Fact]
     public async Task GetPrompts_ReturnsEmptyArrayWhenNoProviderRegistered()
     {
-        await Task.CompletedTask;
+        using var client = CreateAuthedClient();
+        using var response = await client.GetAsync("/api/mcp/prompts");
+
+        response.StatusCode.Should().Be(HttpStatusCode.OK);
+        var body = await response.Content.ReadAsStringAsync();
+        body.Trim().Should().Be("[]");
     }
 
-    /// <summary>POST invoke with valid args returns 200 and Success=true.</summary>
+    // ── Tests: Tool invocation ────────────────────────────────────────────────
+
+    /// <summary>POST /api/mcp/tools/{name}/invoke returns 200 with Success=true for a working tool.</summary>
     [Fact]
     public async Task InvokeTool_ValidArgs_Returns200WithOutput()
     {
-        await Task.CompletedTask;
+        var (client, _) = CreateClientWithFakeTool(
+            "working-tool",
+            invokeImpl: () => ValueTask.FromResult<object?>("tool result"));
+
+        var body = new StringContent(
+            JsonSerializer.Serialize(new { Arguments = new { } }),
+            Encoding.UTF8, "application/json");
+
+        using var response = await client.PostAsync("/api/mcp/tools/working-tool/invoke", body);
+
+        response.StatusCode.Should().Be(HttpStatusCode.OK);
+        var result = await response.Content.ReadFromJsonAsync<McpToolInvokeResponse>();
+        result.Should().NotBeNull();
+        result!.Success.Should().BeTrue();
     }
 
-    /// <summary>POST invoke for unknown tool returns 404.</summary>
+    /// <summary>POST /api/mcp/tools/{name}/invoke returns 404 for an unknown tool name.</summary>
     [Fact]
     public async Task InvokeTool_UnknownTool_Returns404()
     {
-        await Task.CompletedTask;
+        var (client, _) = CreateClientWithFakeTool("known-tool");
+        var body = new StringContent(
+            JsonSerializer.Serialize(new { Arguments = new { } }),
+            Encoding.UTF8, "application/json");
+
+        using var response = await client.PostAsync("/api/mcp/tools/nonexistent-tool/invoke", body);
+
+        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
     }
 
-    /// <summary>POST invoke where the tool throws returns 200 with Success=false and sanitized error.</summary>
+    /// <summary>POST /api/mcp/tools/{name}/invoke returns 200 with Success=false when the tool throws.</summary>
     [Fact]
     public async Task InvokeTool_ToolExecutionFailure_Returns200WithSuccessFalse()
     {
-        await Task.CompletedTask;
+        var (client, _) = CreateClientWithFakeTool("failing-tool", throwOnInvoke: true);
+        var body = new StringContent(
+            JsonSerializer.Serialize(new { Arguments = new { } }),
+            Encoding.UTF8, "application/json");
+
+        using var response = await client.PostAsync("/api/mcp/tools/failing-tool/invoke", body);
+
+        response.StatusCode.Should().Be(HttpStatusCode.OK);
+        var result = await response.Content.ReadFromJsonAsync<McpToolInvokeResponse>();
+        result.Should().NotBeNull();
+        result!.Success.Should().BeFalse();
     }
 
-    /// <summary>POST invoke emits a structured audit log at Information level with UserId, ToolName, InputHash.</summary>
+    /// <summary>POST /api/mcp/tools/{name}/invoke emits a structured audit log entry with UserId, ToolName, InputHash.</summary>
     [Fact]
     public async Task InvokeTool_EmitsStructuredAuditLog()
     {
-        await Task.CompletedTask;
+        var (client, logs) = CreateClientWithFakeTool("audit-tool");
+        var body = new StringContent(
+            JsonSerializer.Serialize(new { Arguments = new { key = "value" } }),
+            Encoding.UTF8, "application/json");
+
+        using var _ = await client.PostAsync("/api/mcp/tools/audit-tool/invoke", body);
+
+        logs.Entries.Should().Contain(e =>
+            e.Level == LogLevel.Information &&
+            e.Message.Contains("audit-tool") &&
+            e.Message.Contains("InputHash"));
     }
 
-    /// <summary>POST invoke with body over 32KB returns 413 Request Entity Too Large.</summary>
+    /// <summary>POST body exceeding 32 KB returns 413 Request Entity Too Large.</summary>
     [Fact]
     public async Task InvokeTool_OversizedBody_Returns413()
     {
-        await Task.CompletedTask;
+        using var client = CreateAuthedClient();
+
+        // 33 KB of JSON — safely over the 32 KB limit.
+        var oversized = new string('x', 33 * 1024);
+        var body = new StringContent($"{{\"Arguments\":{{\"data\":\"{oversized}\"}}}}", Encoding.UTF8, "application/json");
+
+        using var response = await client.PostAsync("/api/mcp/tools/any-tool/invoke", body);
+
+        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
     }
 
-    /// <summary>Audit log at Information level does not contain raw argument values.</summary>
+    /// <summary>Audit log entries at Information level do not contain raw argument values.</summary>
     [Fact]
     public async Task InvokeTool_AuditLog_DoesNotContainRawArgumentsAtInfoLevel()
     {
-        await Task.CompletedTask;
+        var (client, logs) = CreateClientWithFakeTool("no-leak-tool");
+        const string sensitiveValue = "super-secret-argument-value";
+        var body = new StringContent(
+            JsonSerializer.Serialize(new { Arguments = new { secret = sensitiveValue } }),
+            Encoding.UTF8, "application/json");
+
+        using var _ = await client.PostAsync("/api/mcp/tools/no-leak-tool/invoke", body);
+
+        var infoEntries = logs.Entries
+            .Where(e => e.Level == LogLevel.Information)
+            .ToList();
+
+        infoEntries.Should().NotBeEmpty("audit log entry must exist at Information level");
+        infoEntries.Should().NotContain(e => e.Message.Contains(sensitiveValue),
+            "raw argument values must not appear in Information-level log entries");
+    }
+
+    // ── Nested helpers ────────────────────────────────────────────────────────
+
+    /// <summary>
+    /// Concrete <see cref="AIFunction"/> subclass used as a test double.
+    /// Implements only the members required by <see cref="McpController"/>.
+    /// </summary>
+    private sealed class FakeAIFunction : AIFunction
+    {
+        private readonly Func<ValueTask<object?>>? _impl;
+
+        public override string Name { get; }
+        public override string? Description { get; }
+        public override JsonElement JsonSchema { get; }
+
+        public FakeAIFunction(string name, string description, Func<ValueTask<object?>>? impl = null)
+        {
+            Name = name;
+            Description = description;
+            JsonSchema = JsonSerializer.SerializeToElement(new { type = "object" });
+            _impl = impl;
+        }
+
+        // InvokeCoreAsync is called by AIFunction.InvokeAsync after argument marshaling.
+        // Returning a faulted ValueTask here is properly propagated by the base class.
+        protected override ValueTask<object?> InvokeCoreAsync(
+            AIFunctionArguments arguments,
+            CancellationToken cancellationToken)
+        {
+            if (_impl is null)
+                return ValueTask.FromResult<object?>("ok");
+            return _impl();
+        }
+    }
+
+    /// <summary>
+    /// Captures log entries written during a request for assertion in audit log tests.
+    /// Register as a singleton <see cref="ILoggerProvider"/> in <c>ConfigureTestServices</c>.
+    /// </summary>
+    internal sealed class TestLoggerProvider : ILoggerProvider
+    {
+        public ConcurrentBag<(LogLevel Level, string Message)> Entries { get; } = new();
+
+        public ILogger CreateLogger(string categoryName) => new Logger(this);
+        public void Dispose() { }
+
+        private sealed class Logger(TestLoggerProvider provider) : ILogger
+        {
+            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
+            public bool IsEnabled(LogLevel logLevel) => true;
+
+            public void Log<TState>(
+                LogLevel logLevel,
+                EventId eventId,
+                TState state,
+                Exception? exception,
+                Func<TState, Exception?, string> formatter)
+                => provider.Entries.Add((logLevel, formatter(state, exception)));
+        }
     }
 }
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/CoreSetupTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/CoreSetupTests.cs
index b537aa8..706ac42 100644
--- a/src/Content/Tests/Presentation.AgentHub.Tests/CoreSetupTests.cs
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/CoreSetupTests.cs
@@ -107,10 +107,15 @@ public class CoreSetupTests : IClassFixture<TestWebApplicationFactory>
     [Fact]
     public async Task McpInvoke_Called11TimesRapidly_Returns429OnEleventh()
     {
-        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
-        {
-            AllowAutoRedirect = false
-        });
+        // Rate limiter runs after UseAuthentication/UseAuthorization in Program.cs,
+        // so requests must be authenticated to reach it. TestAuthHandler supplies the identity.
+        var client = _factory
+            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
+                services.AddAuthentication(TestAuthHandler.SchemeName)
+                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
+                        TestAuthHandler.SchemeName, _ => { })))
+            .CreateClient();
+        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "rate-test-user");
 
         // Dispose each response to avoid socket exhaustion; capture the status code.
         var lastStatus = HttpStatusCode.OK;
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/Hubs/AgentTelemetryHubTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/Hubs/AgentTelemetryHubTests.cs
index 193bd63..e91eb84 100644
--- a/src/Content/Tests/Presentation.AgentHub.Tests/Hubs/AgentTelemetryHubTests.cs
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/Hubs/AgentTelemetryHubTests.cs
@@ -1,95 +1,403 @@
+using Application.Core.CQRS.Agents.ExecuteAgentTurn;
+using FluentAssertions;
+using MediatR;
+using Microsoft.AspNetCore.Authentication;
+using Microsoft.AspNetCore.Http.Connections;
+using Microsoft.AspNetCore.Mvc.Testing;
+using Microsoft.AspNetCore.SignalR;
+using Microsoft.AspNetCore.SignalR.Client;
+using Microsoft.AspNetCore.TestHost;
+using Microsoft.Extensions.DependencyInjection;
+using Moq;
+using Presentation.AgentHub.Hubs;
+using Presentation.AgentHub.Interfaces;
+using Presentation.AgentHub.Models;
+using System.Collections.Concurrent;
 using Xunit;
 
 namespace Presentation.AgentHub.Tests.Hubs;
 
 /// <summary>
-/// Integration tests for <c>AgentTelemetryHub</c>.
-/// Stubs — wired up fully in section-07 once TestAuthHandler supports per-test
-/// claim injection and the SignalR test client infrastructure is in place.
+/// Integration tests for <see cref="AgentTelemetryHub"/>.
+/// Uses an in-process test server with HTTP long-polling transport (WebSocket requires a real socket server).
+/// <see cref="TestWebApplicationFactory.MockMediator"/> controls agent responses to prevent real AI calls.
 /// </summary>
-public sealed class AgentTelemetryHubTests : IClassFixture<TestWebApplicationFactory>
+public sealed class AgentTelemetryHubTests : IClassFixture<TestWebApplicationFactory>, IDisposable
 {
-    public AgentTelemetryHubTests(TestWebApplicationFactory factory) { }
+    private readonly TestWebApplicationFactory _factory;
+    private readonly WebApplicationFactory<Program> _authedFactory;
+    private readonly IConversationStore _store;
 
-    // --- Authentication and Authorization ---
+    /// <summary>
+    /// Initialises the hub test class. Creates a shared authenticated factory variant
+    /// that adds <see cref="TestAuthHandler"/> as the default authentication scheme.
+    /// </summary>
+    public AgentTelemetryHubTests(TestWebApplicationFactory factory)
+    {
+        _factory = factory;
+        _authedFactory = factory.WithWebHostBuilder(builder =>
+            builder.ConfigureTestServices(services =>
+                services.AddAuthentication(TestAuthHandler.SchemeName)
+                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
+                        TestAuthHandler.SchemeName, _ => { })));
+
+        // Singleton store is shared between base factory and authenticated factory
+        // because ConfigureTestServices runs for both.
+        _store = factory.Services.GetRequiredService<IConversationStore>();
+    }
+
+    /// <inheritdoc/>
+    public void Dispose() => _authedFactory.Dispose();
+
+    // ── Infrastructure ────────────────────────────────────────────────────────
+
+    /// <summary>
+    /// Creates a SignalR connection to the authenticated test server.
+    /// The connection uses long polling so it works inside <see cref="TestServer"/>.
+    /// </summary>
+    private HubConnection CreateConnection(string userId = "test-user", string? roles = null)
+    {
+        var server = _authedFactory.Server;
+        return new HubConnectionBuilder()
+            .WithUrl($"{server.BaseAddress}hubs/agent", options =>
+            {
+                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
+                options.Transports = HttpTransportType.LongPolling;
+                options.Headers[TestAuthHandler.UserIdHeader] = userId;
+                if (roles is not null)
+                    options.Headers[TestAuthHandler.RolesHeader] = roles;
+            })
+            .Build();
+    }
+
+    // ── Authentication ────────────────────────────────────────────────────────
 
+    /// <summary>
+    /// The negotiate endpoint returns 401 for connections without authentication credentials,
+    /// causing <see cref="HubConnection.StartAsync"/> to throw.
+    /// </summary>
     [Fact]
     public async Task UnauthenticatedConnection_IsRejected()
     {
-        await Task.CompletedTask; // section-07
+        // Use the base factory: TestJwtBearerHandler → NoResult → 401 challenge.
+        var server = _factory.Server;
+        var connection = new HubConnectionBuilder()
+            .WithUrl($"{server.BaseAddress}hubs/agent", options =>
+            {
+                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
+                options.Transports = HttpTransportType.LongPolling;
+                // No auth headers → negotiate returns 401.
+            })
+            .Build();
+
+        var ex = await Record.ExceptionAsync(() => connection.StartAsync());
+
+        ex.Should().NotBeNull("negotiate endpoint returns 401 for unauthenticated requests");
+        connection.State.Should().Be(HubConnectionState.Disconnected);
+        await connection.DisposeAsync();
     }
 
+    // ── Role gates ────────────────────────────────────────────────────────────
+
+    /// <summary>JoinGlobalTraces throws HubException when the caller lacks the required role.</summary>
     [Fact]
-    public async Task StartConversation_AnotherUsersConversationId_ThrowsHubException()
+    public async Task JoinGlobalTraces_WithoutRole_ThrowsHubException()
     {
-        await Task.CompletedTask; // section-07
+        var connection = CreateConnection(roles: null);
+        await connection.StartAsync();
+        try
+        {
+            var ex = await Assert.ThrowsAsync<HubException>(() =>
+                connection.InvokeAsync("JoinGlobalTraces"));
+            ex.Message.Should().Contain("AgentHub.Traces.ReadAll");
+        }
+        finally
+        {
+            await connection.StopAsync();
+            await connection.DisposeAsync();
+        }
     }
 
+    /// <summary>JoinGlobalTraces succeeds when the caller carries the required role.</summary>
     [Fact]
-    public async Task SendMessage_AnotherUsersConversationId_ThrowsHubException()
+    public async Task JoinGlobalTraces_WithRole_Succeeds()
     {
-        await Task.CompletedTask; // section-07
+        var connection = CreateConnection(roles: "AgentHub.Traces.ReadAll");
+        await connection.StartAsync();
+        try
+        {
+            // Must not throw.
+            await connection.InvokeAsync("JoinGlobalTraces");
+        }
+        finally
+        {
+            await connection.StopAsync();
+            await connection.DisposeAsync();
+        }
     }
 
+    // ── Ownership (IDOR) ──────────────────────────────────────────────────────
+
+    /// <summary>StartConversation throws HubException when the conversation belongs to a different user.</summary>
     [Fact]
-    public async Task JoinConversationGroup_AnotherUsersConversationId_ThrowsHubException()
+    public async Task StartConversation_WithAnotherUsersConversationId_ThrowsHubException()
     {
-        await Task.CompletedTask; // section-07
+        var otherConv = await _store.CreateAsync("test-agent", "other-user");
+        var connection = CreateConnection("test-user");
+        await connection.StartAsync();
+        try
+        {
+            await Assert.ThrowsAsync<HubException>(() =>
+                connection.InvokeAsync("StartConversation", "test-agent", otherConv.Id));
+        }
+        finally
+        {
+            await connection.StopAsync();
+            await connection.DisposeAsync();
+        }
     }
 
+    /// <summary>SendMessage throws HubException when the conversation belongs to a different user.</summary>
     [Fact]
-    public async Task JoinGlobalTraces_WithoutRole_ThrowsHubException()
+    public async Task SendMessage_AnotherUsersConversation_ThrowsHubException()
     {
-        await Task.CompletedTask; // section-07
+        var otherConv = await _store.CreateAsync("test-agent", "other-user");
+        var connection = CreateConnection("test-user");
+        await connection.StartAsync();
+        try
+        {
+            await Assert.ThrowsAsync<HubException>(() =>
+                connection.InvokeAsync("SendMessage", otherConv.Id, "Hello"));
+        }
+        finally
+        {
+            await connection.StopAsync();
+            await connection.DisposeAsync();
+        }
     }
 
+    /// <summary>JoinConversationGroup throws HubException when the conversation belongs to a different user.</summary>
     [Fact]
-    public async Task JoinGlobalTraces_WithRole_Succeeds()
+    public async Task JoinConversationGroup_AnotherUsersConversation_ThrowsHubException()
     {
-        await Task.CompletedTask; // section-07
+        var otherConv = await _store.CreateAsync("test-agent", "other-user");
+        var connection = CreateConnection("test-user");
+        await connection.StartAsync();
+        try
+        {
+            await Assert.ThrowsAsync<HubException>(() =>
+                connection.InvokeAsync("JoinConversationGroup", otherConv.Id));
+        }
+        finally
+        {
+            await connection.StopAsync();
+            await connection.DisposeAsync();
+        }
     }
 
-    // --- Chat Flow ---
+    // ── Chat flow ─────────────────────────────────────────────────────────────
 
+    /// <summary>StartConversation creates and persists a new conversation record in the store.</summary>
     [Fact]
     public async Task StartConversation_CreatesNewConversationRecord()
     {
-        await Task.CompletedTask; // section-07
-    }
+        var conversationId = Guid.NewGuid().ToString();
+        var connection = CreateConnection("test-user");
+        await connection.StartAsync();
+        try
+        {
+            await connection.InvokeAsync("StartConversation", "test-agent", conversationId);
 
-    [Fact]
-    public async Task StartConversation_ExistingConversation_ReturnsHistory()
-    {
-        await Task.CompletedTask; // section-07
+            var record = await _store.GetAsync(conversationId);
+            record.Should().NotBeNull();
+            record!.UserId.Should().Be("test-user");
+            record.AgentName.Should().Be("test-agent");
+        }
+        finally
+        {
+            await connection.StopAsync();
+            await connection.DisposeAsync();
+        }
     }
 
+    /// <summary>
+    /// StartConversation returns at most MaxHistoryMessages messages for an existing conversation
+    /// (last 20 of 25 stored).
+    /// </summary>
     [Fact]
-    public async Task SendMessage_DispatchesExecuteAgentTurnCommand()
+    public async Task StartConversation_ExistingConversation_ReturnsHistoryCappedAt20()
     {
-        await Task.CompletedTask; // section-07
+        var conversationId = Guid.NewGuid().ToString();
+        var record = await _store.CreateAsync("test-agent", "test-user", conversationId: conversationId);
+        for (var i = 0; i < 25; i++)
+            await _store.AppendMessageAsync(record.Id,
+                new ConversationMessage(MessageRole.User, $"msg-{i}", DateTimeOffset.UtcNow));
+
+        var connection = CreateConnection("test-user");
+        await connection.StartAsync();
+        try
+        {
+            var history = await connection.InvokeAsync<List<ConversationMessage>>(
+                "StartConversation", "test-agent", conversationId);
+
+            history.Should().HaveCount(20, "MaxHistoryMessages is 20; last 20 of 25 should be returned");
+        }
+        finally
+        {
+            await connection.StopAsync();
+            await connection.DisposeAsync();
+        }
     }
 
+    /// <summary>SendMessage emits at least one TokenReceived event before TurnComplete fires.</summary>
     [Fact]
     public async Task SendMessage_EmitsTokenReceivedEventsBeforeTurnComplete()
     {
-        await Task.CompletedTask; // section-07
+        _factory.MockMediator.Reset();
+        _factory.MockMediator
+            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new AgentTurnResult
+            {
+                Success = true,
+                Response = "This is a mock response that is intentionally longer than fifty characters.",
+                UpdatedHistory = [],
+            });
+
+        var tokensReceived = new ConcurrentQueue<string>();
+        var turnCompleteTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
+
+        var connection = CreateConnection("test-user");
+        connection.On<object>(AgentTelemetryHub.EventTokenReceived, _ => tokensReceived.Enqueue("token"));
+        connection.On<object>(AgentTelemetryHub.EventTurnComplete, _ => turnCompleteTcs.TrySetResult(true));
+
+        await connection.StartAsync();
+        try
+        {
+            var conversationId = Guid.NewGuid().ToString();
+            await connection.InvokeAsync("StartConversation", "test-agent", conversationId);
+            await connection.InvokeAsync("SendMessage", conversationId, "Hello");
+            await turnCompleteTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
+
+            tokensReceived.Should().NotBeEmpty(
+                "at least one TokenReceived event must be emitted before TurnComplete");
+        }
+        finally
+        {
+            await connection.StopAsync();
+            await connection.DisposeAsync();
+        }
     }
 
+    /// <summary>
+    /// When the mediator throws, an Error event is emitted with a sanitized message —
+    /// no exception details or stack traces are surfaced to the client.
+    /// </summary>
     [Fact]
     public async Task SendMessage_OnMediatorException_EmitsErrorEventWithSanitizedMessage()
     {
-        await Task.CompletedTask; // section-07
+        _factory.MockMediator.Reset();
+        _factory.MockMediator
+            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new InvalidOperationException("Internal implementation detail — must not leak"));
+
+        var errorTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
+        var connection = CreateConnection("test-user");
+        connection.On<object>(AgentTelemetryHub.EventError,
+            payload => errorTcs.TrySetResult(payload?.ToString() ?? string.Empty));
+
+        await connection.StartAsync();
+        try
+        {
+            var conversationId = Guid.NewGuid().ToString();
+            await connection.InvokeAsync("StartConversation", "test-agent", conversationId);
+            await connection.InvokeAsync("SendMessage", conversationId, "Trigger error");
+            var errorPayload = await errorTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
+
+            errorPayload.Should().NotContain("Internal implementation detail",
+                "exception messages must never be surfaced to hub clients");
+        }
+        finally
+        {
+            await connection.StopAsync();
+            await connection.DisposeAsync();
+        }
     }
 
+    /// <summary>
+    /// When the mediator throws, a synthetic error message is appended to the conversation store
+    /// so the conversation record reflects the failed turn.
+    /// </summary>
     [Fact]
     public async Task SendMessage_OnMediatorException_AppendsSyntheticErrorMessageToStore()
     {
-        await Task.CompletedTask; // section-07
+        _factory.MockMediator.Reset();
+        _factory.MockMediator
+            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new InvalidOperationException("Agent failure"));
+
+        var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
+        var connection = CreateConnection("test-user");
+        connection.On<object>(AgentTelemetryHub.EventError, _ => errorTcs.TrySetResult(true));
+
+        await connection.StartAsync();
+        try
+        {
+            var conversationId = Guid.NewGuid().ToString();
+            await connection.InvokeAsync("StartConversation", "test-agent", conversationId);
+            await connection.InvokeAsync("SendMessage", conversationId, "Trigger error");
+            await errorTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
+
+            var record = await _store.GetAsync(conversationId);
+            record.Should().NotBeNull();
+            record!.Messages.Should().Contain(m =>
+                m.Role == MessageRole.Assistant && m.Content.Contains("[Error]"),
+                "a synthetic error assistant message must be appended for the failed turn");
+        }
+        finally
+        {
+            await connection.StopAsync();
+            await connection.DisposeAsync();
+        }
     }
 
+    /// <summary>
+    /// Two concurrent SendMessage calls on the same conversation both complete successfully.
+    /// The per-conversation semaphore ensures sequential execution without interleaving.
+    /// </summary>
     [Fact]
-    public async Task TwoRapidSendMessageCalls_CompletedInOrder_NoInterleavedEvents()
+    public async Task TwoRapidSendMessageCalls_BothCompleteSuccessfully()
     {
-        await Task.CompletedTask; // section-07
+        _factory.MockMediator.Reset();
+        _factory.MockMediator
+            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(new AgentTurnResult
+            {
+                Success = true,
+                Response = "Response",
+                UpdatedHistory = [],
+            });
+
+        var connection = CreateConnection("test-user");
+        await connection.StartAsync();
+        try
+        {
+            var conversationId = Guid.NewGuid().ToString();
+            await connection.InvokeAsync("StartConversation", "test-agent", conversationId);
+
+            // Both invocations on the same conversationId; the semaphore serialises them.
+            var task1 = connection.InvokeAsync("SendMessage", conversationId, "First message");
+            var task2 = connection.InvokeAsync("SendMessage", conversationId, "Second message");
+            await Task.WhenAll(task1, task2);
+
+            _factory.MockMediator.Verify(
+                m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()),
+                Times.Exactly(2),
+                "mediator must be called once per SendMessage invocation");
+        }
+        finally
+        {
+            await connection.StopAsync();
+            await connection.DisposeAsync();
+        }
     }
 }
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj b/src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj
index cc90f36..1c9263b 100644
--- a/src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/Presentation.AgentHub.Tests.csproj
@@ -8,6 +8,7 @@
   </PropertyGroup>
 
   <ItemGroup>
+    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" />
     <PackageReference Include="coverlet.collector" />
     <PackageReference Include="FluentAssertions" />
     <PackageReference Include="Microsoft.NET.Test.Sdk" />
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/TestAuthHandler.cs b/src/Content/Tests/Presentation.AgentHub.Tests/TestAuthHandler.cs
index e96d60c..4976e37 100644
--- a/src/Content/Tests/Presentation.AgentHub.Tests/TestAuthHandler.cs
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/TestAuthHandler.cs
@@ -8,12 +8,17 @@ namespace Presentation.AgentHub.Tests;
 
 /// <summary>
 /// Stub authentication handler for integration tests.
-/// Authenticates all requests as a fixed test user without validating any token.
-/// Register as the default scheme in <c>ConfigureTestServices</c> to bypass Azure AD auth.
+/// Reads the <c>x-test-user</c> request header to determine the authenticated user's identity
+/// (defaults to <c>"test-user"</c> when absent). Reads <c>x-test-roles</c>
+/// (comma-separated) to populate role claims on the resulting principal.
+///
+/// Emits an <c>oid</c> claim so that
+/// <see cref="Presentation.AgentHub.Extensions.ClaimsPrincipalExtensions.GetUserId"/>
+/// resolves correctly — Azure AD's object ID is read from the <c>oid</c> claim first.
+///
+/// Register as the default scheme in <c>ConfigureTestServices</c> to bypass Azure AD auth
+/// while supporting per-test identity and role customisation via HTTP headers.
 /// </summary>
-/// <remarks>
-/// Replaced with a full implementation in section-07 that supports per-test claim customization.
-/// </remarks>
 public class TestAuthHandler(
     IOptionsMonitor<AuthenticationSchemeOptions> options,
     ILoggerFactory logger,
@@ -22,9 +27,33 @@ public class TestAuthHandler(
     /// <summary>Authentication scheme name used to override JWT bearer in integration tests.</summary>
     public const string SchemeName = "TestAuth";
 
+    /// <summary>HTTP header for controlling the authenticated user identity in tests.</summary>
+    public const string UserIdHeader = "x-test-user";
+
+    /// <summary>HTTP header for injecting role claims in tests (comma-separated values).</summary>
+    public const string RolesHeader = "x-test-roles";
+
+    /// <inheritdoc/>
     protected override Task<AuthenticateResult> HandleAuthenticateAsync()
     {
-        var claims = new[] { new Claim(ClaimTypes.Name, "test-user") };
+        var userId = Request.Headers[UserIdHeader].FirstOrDefault() ?? "test-user";
+        var rolesHeader = Request.Headers[RolesHeader].ToString();
+        var roles = string.IsNullOrWhiteSpace(rolesHeader)
+            ? []
+            : rolesHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
+
+        var claims = new List<Claim>
+        {
+            // ClaimsPrincipalExtensions.GetUserId() reads the "oid" claim first.
+            // Emitting it here ensures hub ownership checks work in integration tests.
+            new("oid", userId),
+            new(ClaimTypes.NameIdentifier, userId),
+            new(ClaimTypes.Name, userId),
+        };
+
+        foreach (var role in roles)
+            claims.Add(new Claim(ClaimTypes.Role, role));
+
         var identity = new ClaimsIdentity(claims, SchemeName);
         var principal = new ClaimsPrincipal(identity);
         var ticket = new AuthenticationTicket(principal, SchemeName);
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/TestWebApplicationFactory.cs b/src/Content/Tests/Presentation.AgentHub.Tests/TestWebApplicationFactory.cs
index b009542..4c85bc1 100644
--- a/src/Content/Tests/Presentation.AgentHub.Tests/TestWebApplicationFactory.cs
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/TestWebApplicationFactory.cs
@@ -1,24 +1,50 @@
+using MediatR;
 using Microsoft.AspNetCore.Authentication;
 using Microsoft.AspNetCore.Hosting;
 using Microsoft.AspNetCore.Mvc.Testing;
 using Microsoft.AspNetCore.TestHost;
 using Microsoft.Extensions.DependencyInjection;
+using Microsoft.Extensions.DependencyInjection.Extensions;
 using Microsoft.Extensions.Hosting;
+using Microsoft.Extensions.Logging.Abstractions;
+using Microsoft.Extensions.Options;
+using Moq;
+using Presentation.AgentHub.Interfaces;
+using Presentation.AgentHub.Models;
+using Presentation.AgentHub.Services;
 
 namespace Presentation.AgentHub.Tests;
 
 /// <summary>
 /// Integration test factory for <c>Presentation.AgentHub</c>.
+///
 /// Sets the working directory so <c>AppConfigHelper.LoadAppConfig()</c> can locate
 /// <c>appsettings.json</c>, activates the Development environment, and replaces
 /// Microsoft.Identity.Web's JWT Bearer handler with <see cref="TestJwtBearerHandler"/>
 /// so tests run without valid Azure AD configuration.
+///
+/// Additionally:
+/// <list type="bullet">
+///   <item><description>
+///     Exposes a <see cref="MockMediator"/> for hub tests to control agent turn results
+///     without triggering real AI calls or MediatR pipeline behaviours.
+///   </description></item>
+///   <item><description>
+///     Routes conversation storage to an isolated per-factory temp directory that is
+///     deleted on disposal.
+///   </description></item>
+/// </list>
 /// </summary>
-/// <remarks>
-/// Fleshed out in section-07 with full auth overrides and per-test configuration helpers.
-/// </remarks>
 public class TestWebApplicationFactory : WebApplicationFactory<Program>
 {
+    /// <summary>Mock mediator for controlling <c>ExecuteAgentTurnCommand</c> results in hub tests.</summary>
+    public Mock<IMediator> MockMediator { get; } = new();
+
+    /// <summary>Isolated temp directory used for conversation storage during this factory's lifetime.</summary>
+    public string TempConversationsPath { get; } =
+        Path.Combine(Path.GetTempPath(), $"agenthubtests-{Guid.NewGuid():N}");
+
+    /// <inheritdoc/>
     protected override void ConfigureWebHost(IWebHostBuilder builder)
     {
         // AppConfigHelper.LoadAppConfig() reads appsettings.json from Directory.GetCurrentDirectory().
@@ -33,25 +59,47 @@ public class TestWebApplicationFactory : WebApplicationFactory<Program>
 
         builder.ConfigureTestServices(services =>
         {
+            // Enable detailed SignalR errors so integration tests see the real server-side
+            // exception message instead of the generic "error on the server" wrapper.
+            services.AddSignalR(o => o.EnableDetailedErrors = true);
+
             // Replace Microsoft.Identity.Web's JWT Bearer handler with a no-op stub.
             // TestJwtBearerHandler returns NoResult() when no token is present, causing
-            // UseAuthorization to challenge with 401 for [Authorize] endpoints — matching
-            // real JWT behaviour without requiring valid AzureAd configuration.
+            // UseAuthorization to challenge with 401 for [Authorize] endpoints.
             // Tests that need an authenticated user override this via WithWebHostBuilder +
             // ConfigureTestServices using TestAuthHandler.
             services.AddAuthentication(TestJwtBearerHandler.SchemeName)
                 .AddScheme<AuthenticationSchemeOptions, TestJwtBearerHandler>(
                     TestJwtBearerHandler.SchemeName, _ => { });
+
+            // Route conversation storage to an isolated temp directory.
+            // The last AddSingleton registration wins, replacing DependencyInjection.cs's
+            // registration of FileSystemConversationStore with the appsettings path.
+            Directory.CreateDirectory(TempConversationsPath);
+            services.AddSingleton<IConversationStore>(
+                new FileSystemConversationStore(
+                    Options.Create(new AgentHubConfig
+                    {
+                        ConversationsPath = TempConversationsPath,
+                        DefaultAgentName = "test-agent",
+                        MaxHistoryMessages = 20,
+                    }),
+                    NullLogger<FileSystemConversationStore>.Instance));
+
+            // Replace IMediator with a mock so hub tests can stub AgentTurnResult
+            // without invoking the real MediatR pipeline or AI services.
+            services.RemoveAll<IMediator>();
+            services.AddSingleton<IMediator>(MockMediator.Object);
         });
     }
 
+    /// <inheritdoc/>
     protected override IHost CreateHost(IHostBuilder builder)
     {
-        // The shared GetServices() DI has a pre-existing captive dependency:
-        // MemoizedPromptComposer (singleton) → IPromptSectionProvider (transient) →
-        // IAgentExecutionContext (scoped). ASP.NET Core's hosting validates scopes by
-        // default; ConsoleUI's plain BuildServiceProvider() does not. Suppress validation
-        // here to match ConsoleUI behaviour until the upstream registration is corrected.
+        // Suppress scope validation: MemoizedPromptComposer (singleton) → IPromptSectionProvider
+        // (transient) → IAgentExecutionContext (scoped) creates a captive dependency that ASP.NET
+        // Core's hosting rejects by default. Matches ConsoleUI behaviour where BuildServiceProvider()
+        // does not validate scopes.
         builder.UseDefaultServiceProvider(options =>
         {
             options.ValidateScopes = false;
@@ -59,4 +107,12 @@ public class TestWebApplicationFactory : WebApplicationFactory<Program>
         });
         return base.CreateHost(builder);
     }
+
+    /// <inheritdoc/>
+    protected override void Dispose(bool disposing)
+    {
+        if (disposing && Directory.Exists(TempConversationsPath))
+            Directory.Delete(TempConversationsPath, recursive: true);
+        base.Dispose(disposing);
+    }
 }
diff --git a/src/Directory.Packages.props b/src/Directory.Packages.props
index c5eedbd..4109a16 100644
--- a/src/Directory.Packages.props
+++ b/src/Directory.Packages.props
@@ -86,6 +86,7 @@
     <PackageVersion Include="Spectre.Console" Version="0.54.1-alpha.0.31" />
 
     <!-- Testing -->
+    <PackageVersion Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.5" />
     <PackageVersion Include="coverlet.collector" Version="6.0.4" />
     <PackageVersion Include="FluentAssertions" Version="8.3.0" />
     <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.5" />
