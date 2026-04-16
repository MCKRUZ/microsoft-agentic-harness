# Section 07 — Presentation.AgentHub Tests

## Overview

This section implements the test infrastructure and integration/unit tests for `Presentation.AgentHub`. It is the final AgentHub section and depends on sections 02 through 06 being complete. The test project was scaffolded in section 01; this section fills it with actual tests.

**Verify with:** `dotnet test src/AgenticHarness.slnx`

**Dependencies:** Sections 02 (core setup), 03 (conversation store + AgentsController), 04 (SignalR hub), 05 (OTel bridge), 06 (MCP API)

---

## Test Project Location

```
src/Content/Tests/Presentation.AgentHub.Tests/
  Presentation.AgentHub.Tests.csproj
  TestWebApplicationFactory.cs
  TestAuthHandler.cs
  Hub/
    AgentTelemetryHubTests.cs
  Store/
    FileSystemConversationStoreTests.cs
  Controllers/
    AgentsControllerTests.cs
    McpControllerTests.cs
  Bridge/
    SignalRSpanExporterTests.cs
```

The `.csproj` has package references to:
- `Microsoft.AspNetCore.Mvc.Testing`
- `xunit`, `xunit.runner.visualstudio`
- `Moq`
- `coverlet.collector`

And project references to `Presentation.AgentHub` and `Presentation.Common`.

---

## Test Infrastructure

### TestWebApplicationFactory

File: `src/Content/Tests/Presentation.AgentHub.Tests/TestWebApplicationFactory.cs`

Subclasses `WebApplicationFactory<Program>`. In `ConfigureWebHost`:

1. **Replace Azure AD auth** — remove the `JwtBearer` scheme and add `TestAuthHandler` under the same scheme name used by `AddMicrosoftIdentityWebApi`. Use `services.PostConfigure<AuthenticationOptions>(...)` to set the default authenticate/challenge scheme to `"Test"`.
2. **Use a temp directory** — override the `AgentHubConfig.ConversationsPath` binding so `FileSystemConversationStore` writes to `Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())`. Clean up in `Dispose`.
3. **Register a mock `IMediator`** — remove the real MediatR registration and add a `Mock<IMediator>` singleton so hub tests can control what `Send` returns without making real AI calls.

The factory exposes a `MockMediator` property of type `Mock<IMediator>` so individual tests can set up expectations.

Stub signature:

```csharp
/// <summary>
/// WebApplicationFactory for integration tests. Replaces Azure AD auth with
/// <see cref="TestAuthHandler"/>, uses a temp directory for conversation storage,
/// and registers a mock IMediator to prevent real AI calls.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public Mock<IMediator> MockMediator { get; } = new();
    public string TempConversationsPath { get; } = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    protected override void ConfigureWebHost(IWebHostBuilder builder) { /* ... */ }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() { /* delete TempConversationsPath */ }
}
```

### TestAuthHandler

File: `src/Content/Tests/Presentation.AgentHub.Tests/TestAuthHandler.cs`

An `AuthenticationHandler<AuthenticationSchemeOptions>` that:
- Reads an `x-test-user` request header; defaults to `"test-user"` if absent.
- Reads an `x-test-roles` header (comma-separated) to populate role claims.
- Returns `AuthenticateResult.Success(ticket)` with a `ClaimsPrincipal` containing `NameIdentifier`, `Name`, and any role claims.

This allows individual tests to simulate different users (for IDOR tests) and different role sets (for the `AgentHub.Traces.ReadAll` gate) by setting custom headers on `HttpClient` requests or SignalR connection options.

Stub signature:

```csharp
/// <summary>
/// Test authentication handler. Authenticates every request as a configurable
/// test user. Set x-test-user header to specify user identity; x-test-roles for role claims.
/// </summary>
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() { /* ... */ }
}
```

---

## Tests: Self-Verification (Section 7 meta-tests)

These verify the infrastructure itself works before trusting any other test results.

| Test | Assertion |
|------|-----------|
| `TestWebApplicationFactory_StartsWithoutErrors` | Factory creates HTTP client and `GET /health` (or any endpoint) responds without throwing |
| `TestAuthHandler_ReturnsAuthenticatedPrincipal` | Any authenticated request returns a non-null `User.Identity.Name` |
| `TestWebApplicationFactory_UsesTempDirectoryForStore` | `TempConversationsPath` is a real path distinct from the system conversations directory |

---

## Tests: Hub Authentication and Authorization

File: `src/Content/Tests/Presentation.AgentHub.Tests/Hub/AgentTelemetryHubTests.cs`

Use `WebApplicationFactory` + a real `HubConnection` pointed at the in-process test server via `server.GetTestServer().CreateHandler()` (or `server.CreateDefaultClient()` base address). The SignalR client connects over HTTP/1.1 long polling in tests (WebSockets require a real socket server).

| Test | Setup | Assertion |
|------|-------|-----------|
| `UnauthenticatedConnection_IsRejected` | No auth header | `HubConnection.StartAsync` throws or connection state becomes `Disconnected` immediately |
| `StartConversation_CreatesConversationRecord` | Auth as `"test-user"` | After `StartConversation(conversationId)`, `FileSystemConversationStore.GetAsync(conversationId)` returns non-null |
| `StartConversation_WithAnotherUsersConversationId_ThrowsHubException` | Pre-create conversation owned by `"other-user"`; connect as `"test-user"` | `StartConversation` throws `HubException` |
| `SendMessage_OnAnotherUsersConversation_ThrowsHubException` | Same IDOR setup | `SendMessage` throws `HubException` |
| `JoinConversationGroup_OnAnotherUsersConversation_ThrowsHubException` | Same IDOR setup | `JoinConversationGroup` throws `HubException` |
| `JoinGlobalTraces_WithoutRole_ThrowsHubException` | Auth with no roles | `JoinGlobalTraces` throws `HubException` |
| `JoinGlobalTraces_WithRole_Succeeds` | Auth with `x-test-roles: AgentHub.Traces.ReadAll` | `JoinGlobalTraces` completes without exception |
| `SendMessage_EmitsTokenReceivedBeforeTurnComplete` | Mock mediator returns streaming result | `TokenReceived` events received before `TurnComplete` event |
| `SendMessage_OnMediatorException_EmitsErrorEvent` | Mock mediator throws | `Error` event received with sanitized (non-stack-trace) message |
| `SendMessage_OnMediatorException_AppendsSyntheticErrorMessage` | Mock mediator throws | Conversation record contains a synthetic error assistant message |
| `TwoRapidSendMessages_CompleteInOrder` | Two parallel `SendMessage` invocations | Events for message 1 all precede events for message 2 (turn serialization via semaphore) |
| `StartConversation_OnExistingConversation_ReturnsLast20Messages` | Pre-populate conversation with 25 messages | Response/events include exactly 20 messages |

---

## Tests: Conversation Store

File: `src/Content/Tests/Presentation.AgentHub.Tests/Store/FileSystemConversationStoreTests.cs`

Unit tests. Instantiate `FileSystemConversationStore` directly with a fresh temp directory per test (use `IClassFixture` or inline `Path.GetTempPath()`). No factory needed.

| Test | Assertion |
|------|-----------|
| `CreateAsync_WritesJsonFileAtExpectedPath` | File exists at `{root}/{userId}/{conversationId}.json` |
| `GetAsync_ReturnsNullForUnknownConversationId` | Returns `null` |
| `GetAsync_DeserializesConversationRecordCorrectly` | Round-trips `Title`, `Messages`, `OwnerId` |
| `AppendMessageAsync_UpdatesFileAtomically` | Uses tmp→move pattern; file is never partially written (verify by checking no tmp files remain) |
| `DeleteAsync_RemovesFile` | File no longer exists |
| `ListAsync_ReturnsOnlyConversationsBelongingToUserId` | Create two users' conversations; only caller's appear |
| `ConcurrentAppendMessageAsync_DoesNotCorruptFile` | 20 parallel tasks appending to same conversation; final message count matches |
| `PathTraversalInConversationId_ThrowsArgumentException` | `conversationId = "../../../etc/passwd"` throws `ArgumentException` |
| `GetHistoryForDispatch_ReturnsAtMostMaxMessages` | 30 messages stored; `GetHistoryForDispatch(20)` returns 20 |
| `GetHistoryForDispatch_ReturnsLastNMessages_NotFirstN` | 30 messages stored; returned 20 are the last 20 by index |

---

## Tests: AgentsController

File: `src/Content/Tests/Presentation.AgentHub.Tests/Controllers/AgentsControllerTests.cs`

Use `TestWebApplicationFactory`. Set `x-test-user` header on `HttpClient` to simulate different users.

| Test | Assertion |
|------|-----------|
| `GetConversations_ReturnsOnlyConversationsOwnedByAuthenticatedUser` | User A's conversations not visible to user B |
| `GetConversation_ForAnotherUsersConversation_Returns403` | HTTP 403 |
| `DeleteConversation_ForAnotherUsersConversation_Returns403` | HTTP 403 |
| `DeleteConversation_ForOwnConversation_Returns204AndRemovesFile` | HTTP 204; file gone from temp directory |

---

## Tests: MCP Controller

File: `src/Content/Tests/Presentation.AgentHub.Tests/Controllers/McpControllerTests.cs`

Use `TestWebApplicationFactory` with authenticated client.

| Test | Assertion |
|------|-----------|
| `GetTools_Returns200WithToolList` | Status 200; response is a JSON array |
| `GetTools_ReturnsToolNameDescriptionAndSchema` | Each tool object has `name`, `description`, `inputSchema` fields |
| `GetPrompts_ReturnsEmptyArrayWhenNoProviderRegistered` | Status 200; body is `[]` — not 500 |
| `InvokeTool_WithValidArgs_Returns200WithOutput` | Status 200; `Success=true` in response |
| `InvokeTool_Nonexistent_Returns404` | HTTP 404 |
| `InvokeTool_WithToolExecutionError_Returns200WithSuccessFalse` | HTTP 200; `Success=false` |
| `InvokeTool_EmitsStructuredAuditLogEntry` | Log sink captures a structured log entry with tool name and input hash |
| `InvokeTool_BodyExceeding32KB_Returns413` | HTTP 413 |
| `InvokeTool_AuditLog_DoesNotIncludeRawArgumentsAtInformationLevel` | Log entries at `Information` level do not contain the raw input JSON |

To verify audit log behavior, register a custom `ILoggerProvider` in the factory that captures `LogEntry` records, then assert on captured entries after the request.

---

## Tests: OTel Bridge

File: `src/Content/Tests/Presentation.AgentHub.Tests/Bridge/SignalRSpanExporterTests.cs`

Unit tests. Instantiate `SignalRSpanExporter` directly, injecting a mock `IHubContext<AgentTelemetryHub, IAgentTelemetryHubClient>`.

| Test | Assertion |
|------|-----------|
| `Export_WithFullChannel_DoesNotBlock` | Fill channel to capacity, call `Export` again; returns within 1ms |
| `Export_WhenChannelFull_LogsWarning` | Warning logged with "dropped" or "full" in message |
| `MapToSpanData_SetsParentSpanIdNullForRootSpans` | Span with no parent maps to `ParentSpanId = null` |
| `MapToSpanData_ExtractsConversationIdTag` | Span with `agent.conversation_id` tag maps to `ConversationId` field |
| `MapToSpanData_SetsConversationIdNullWhenTagAbsent` | Span without tag maps to `ConversationId = null` |
| `DrainLoop_SpanWithConversationId_SentToConversationGroup` | Mock hub client receives `SendToConversationGroup` call with correct `conversationId` |
| `DrainLoop_SpanAlwaysSentToGlobalTracesGroup` | Mock hub client always receives `SendToGlobalTracesGroup` call regardless of `ConversationId` |
| `StopAsync_CompletesChannelAndDrainLoopExitsCleanly` | `StopAsync` returns within 2 seconds; no hung tasks |

---

## Coverage Target

All test files together must produce ≥ 80% line coverage over `Presentation.AgentHub`. Run:

```
dotnet test src/AgenticHarness.slnx --collect:"XPlat Code Coverage"
```

The Coverlet report is generated under `TestResults/`. If coverage drops below 80%, add targeted unit tests for any uncovered paths before marking this section complete.

---

## XML Documentation

All public types in the test project (`TestWebApplicationFactory`, `TestAuthHandler`, test classes) must carry full XML `<summary>` documentation. Test projects are teaching material — the doc explains the intent of the helper, not just what it is.

---

## Actual Implementation Notes

**Deviations from plan:**

1. **Folder structure — `Hub/` renamed to `Hubs/`** — plural to match `Presentation.AgentHub` conventions.

2. **AgentTelemetryHub: CancellationToken removed from all public hub methods** — .NET 10 SignalR counts `CancellationToken ct = default` parameters as expected client-side arguments, causing "Invocation provides N argument(s) but target expects N+1" failures. All `ct` parameters were removed from public hub method signatures; `var ct = Context.ConnectionAborted;` is declared at the top of each method body instead.

3. **McpController: manual ContentLength check** — `[RequestSizeLimit(32 * 1024)]` attribute does not function under `TestServer` (which doesn't implement `IHttpMaxRequestBodySizeFeature`). A manual `if (Request.ContentLength > 32 * 1024)` guard was added at the top of `InvokeTool`, enabling `InvokeTool_OversizedBody_Returns413` to pass.

4. **McpToolInvokeResponse.Output: changed from `JsonElement` to `JsonElement?`** — The non-nullable struct default (`ValueKind == Undefined`) throws `InvalidOperationException` during `System.Text.Json` serialization when no output is set (error path). Changed to `JsonElement?`; null serializes correctly.

5. **FakeAIFunction: only overrides `InvokeCoreAsync`** — `AIFunction.InvokeAsync` is not marked `virtual` in the installed SDK version. The fake implements logic in `InvokeCoreAsync` only; the base class's `InvokeAsync` calls through to it.

6. **TestAuthHandler uses `oid` claim** — `ClaimsPrincipalExtensions.GetUserId()` reads the `"oid"` claim. `TestAuthHandler` emits that claim (not `NameIdentifier`) to match production identity extraction.

7. **Rate limiter test requires authenticated client** — `UseRateLimiter()` is placed after `UseAuthentication()` / `UseAuthorization()` in `Program.cs`, so unauthenticated requests get 401 before reaching the limiter. `McpInvoke_Called11TimesRapidly_Returns429OnEleventh` registers `TestAuthHandler` to authenticate the 11 requests.

8. **`EnableDetailedErrors = true` added to test factory** — Without this, hub test failures surface as generic "error on the server" messages. Added in `TestWebApplicationFactory.ConfigureWebHost` via `services.AddSignalR(o => o.EnableDetailedErrors = true)`.

**Test count:** 52 tests, all passing.

**Files created/modified:**
- `TestWebApplicationFactory.cs` — created
- `TestAuthHandler.cs` — created
- `Hubs/AgentTelemetryHubTests.cs` — created
- `Controllers/AgentsControllerTests.cs` — created
- `Controllers/McpControllerTests.cs` — created
- `CoreSetupTests.cs` — modified (rate limiter test now authenticated)
- `Presentation.AgentHub.Tests.csproj` — added `Microsoft.AspNetCore.SignalR.Client` package reference
- `Presentation.AgentHub/Hubs/AgentTelemetryHub.cs` — CancellationToken params removed from hub methods
- `Presentation.AgentHub/Controllers/McpController.cs` — manual ContentLength guard added
- `Presentation.AgentHub/Models/McpToolInvokeResponse.cs` — `Output` changed to `JsonElement?`
