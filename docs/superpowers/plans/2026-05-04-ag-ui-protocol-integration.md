# AG-UI Protocol Integration Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add AG-UI protocol support to the Agentic Harness, providing a standardized SSE-based streaming endpoint for agent conversations alongside the existing SignalR transport.

**Architecture:** Manual AG-UI SSE endpoint in `Presentation.AgentHub` using ASP.NET Core minimal APIs. Server-authoritative conversation management (server persists and loads history; client sends latest user message only in practice). React frontend uses `@ag-ui/client` for the streaming conversation flow, retaining SignalR for conversation lifecycle operations and telemetry.

**Tech Stack:** ASP.NET Core minimal APIs, Server-Sent Events (SSE), System.Text.Json, `@ag-ui/client` 0.0.53, `@ag-ui/core` 0.0.53, RxJS, xUnit, FluentAssertions, Moq

---

## Design Decisions

1. **Manual SSE over `MapAGUI` package** -- `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` (v1.3.0-preview) assumes a simple `IChatClient`-to-endpoint model. Our architecture (MediatR dispatch, conversation persistence, user ownership validation, per-conversation concurrency locks) requires more control. The AG-UI wire format is simple enough (HTTP POST in, SSE out, typed JSON events) to implement directly. If the package stabilizes later, swapping in the hosting layer is low-cost since the event format is identical.

2. **Server-authoritative history** -- AG-UI clients send full message history per the protocol. Our server loads its own persisted conversation and uses only the **latest user message** from the client's `messages` array. This prevents history manipulation while remaining wire-compatible with any AG-UI client.

3. **Dual transport (incremental migration)** -- AG-UI handles the primary streaming path (`sendMessage` -> token stream -> completion). SignalR retains conversation lifecycle (`StartConversation`, `RetryFromMessage`, `EditAndResubmit`, `SetConversationSettings`), tool invocation, and telemetry (`SpanReceived`, tool call events). No big-bang cutover.

4. **No Application layer changes** -- The AG-UI handler dispatches to the existing `ExecuteAgentTurnCommand` via MediatR, same as the SignalR hub does today. Transport adaptation lives entirely in Presentation.

5. **AG-UI endpoint scope** -- The `/ag-ui/run` endpoint covers one operation: send a user message and stream the agent's response. Conversation creation stays on REST (`POST /api/conversations`). The client flow becomes: create conversation via REST, then stream messages via AG-UI SSE.

---

## File Structure

### New Files (Backend -- `src/Content/Presentation/Presentation.AgentHub/`)

| File | Responsibility |
|------|---------------|
| `AgUi/AgUiEventType.cs` | String constants for the 17 AG-UI event type discriminators |
| `AgUi/AgUiEvents.cs` | Immutable record types for each AG-UI event |
| `AgUi/AgUiModels.cs` | `RunAgentInput` and `AgUiMessage` request DTOs |
| `AgUi/AgUiEventWriter.cs` | `IAgUiEventWriter` interface + implementation that writes SSE frames to `HttpResponse.Body` |
| `AgUi/AgUiRunHandler.cs` | Orchestration: validate -> lock -> dispatch to MediatR -> emit AG-UI events |
| `AgUi/AgUiEndpoints.cs` | Minimal API `MapPost("/ag-ui/run", ...)` endpoint registration |

### New Files (Frontend -- `src/Content/Presentation/Presentation.WebUI/src/`)

| File | Responsibility |
|------|---------------|
| `lib/agUiClient.ts` | `HttpAgent` factory with MSAL bearer token injection |
| `hooks/useAgentStream.ts` | AG-UI conversation hook -- replaces SignalR `sendMessage` for the streaming path |

### Modified Files

| File | Change |
|------|--------|
| `Presentation.AgentHub/DependencyInjection.cs` | Register `AgUiRunHandler` as scoped service |
| `Presentation.AgentHub/Program.cs` | Map AG-UI endpoints via `app.MapAgUiEndpoints()` |
| `Presentation.WebUI/package.json` | Add `@ag-ui/client`, `@ag-ui/core`, `rxjs` |
| `Presentation.WebUI/src/hooks/useAgentHub.tsx` | Remove `sendMessage` (now on AG-UI); keep lifecycle + telemetry |
| `Presentation.WebUI/src/features/chat/ChatPanel.tsx` | Use `useAgentStream` for message sending |

### Test Files (`src/Content/Tests/Presentation.AgentHub.Tests/`)

| File | Coverage |
|------|----------|
| `AgUi/AgUiEventSerializationTests.cs` | Event JSON format matches AG-UI spec |
| `AgUi/AgUiEventWriterTests.cs` | SSE frame format (`data: {json}\n\n`) |
| `AgUi/AgUiRunHandlerTests.cs` | Orchestration logic: happy path, auth failure, conversation not found, agent error |

---

### Task 1: AG-UI Event Model

**Files:**
- Create: `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs`
- Create: `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs`
- Test: `src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEventSerializationTests.cs`

- [ ] **Step 1: Write event serialization tests**

```csharp
// src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEventSerializationTests.cs
using System.Text.Json;
using FluentAssertions;
using Presentation.AgentHub.AgUi;

namespace Presentation.AgentHub.Tests.AgUi;

public class AgUiEventSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void RunStartedEvent_Serializes_WithCorrectTypeDiscriminator()
    {
        var evt = new RunStartedEvent("thread-1", "run-1");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"RUN_STARTED\"");
        json.Should().Contain("\"threadId\":\"thread-1\"");
        json.Should().Contain("\"runId\":\"run-1\"");
    }

    [Fact]
    public void TextMessageContentEvent_Serializes_WithDelta()
    {
        var evt = new TextMessageContentEvent("msg-1", "Hello ");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"TEXT_MESSAGE_CONTENT\"");
        json.Should().Contain("\"messageId\":\"msg-1\"");
        json.Should().Contain("\"delta\":\"Hello \"");
    }

    [Fact]
    public void TextMessageStartEvent_Serializes_WithRole()
    {
        var evt = new TextMessageStartEvent("msg-1", "assistant");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"TEXT_MESSAGE_START\"");
        json.Should().Contain("\"role\":\"assistant\"");
    }

    [Fact]
    public void RunErrorEvent_Serializes_WithMessage()
    {
        var evt = new RunErrorEvent("Something went wrong");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"RUN_ERROR\"");
        json.Should().Contain("\"message\":\"Something went wrong\"");
    }

    [Fact]
    public void RunFinishedEvent_Serializes_MatchingRunStarted()
    {
        var evt = new RunFinishedEvent("thread-1", "run-1");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"RUN_FINISHED\"");
        json.Should().Contain("\"threadId\":\"thread-1\"");
        json.Should().Contain("\"runId\":\"run-1\"");
    }

    [Fact]
    public void TextMessageEndEvent_Serializes_WithMessageId()
    {
        var evt = new TextMessageEndEvent("msg-1");
        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);

        json.Should().Contain("\"type\":\"TEXT_MESSAGE_END\"");
        json.Should().Contain("\"messageId\":\"msg-1\"");
    }
}
```

- [ ] **Step 2: Run tests -- expect compile failure**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "FullyQualifiedName~AgUiEventSerialization" --no-build 2>&1 | head -5`
Expected: Build failure -- `AgUiEvent`, `RunStartedEvent`, etc. not found.

- [ ] **Step 3: Create AG-UI event type constants**

```csharp
// src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs
namespace Presentation.AgentHub.AgUi;

/// <summary>
/// AG-UI protocol event type discriminators.
/// Values match the AG-UI specification wire format.
/// </summary>
public static class AgUiEventType
{
    public const string RunStarted = "RUN_STARTED";
    public const string RunFinished = "RUN_FINISHED";
    public const string RunError = "RUN_ERROR";
    public const string StepStarted = "STEP_STARTED";
    public const string StepFinished = "STEP_FINISHED";
    public const string TextMessageStart = "TEXT_MESSAGE_START";
    public const string TextMessageContent = "TEXT_MESSAGE_CONTENT";
    public const string TextMessageEnd = "TEXT_MESSAGE_END";
    public const string ToolCallStart = "TOOL_CALL_START";
    public const string ToolCallArgs = "TOOL_CALL_ARGS";
    public const string ToolCallEnd = "TOOL_CALL_END";
    public const string ToolCallResult = "TOOL_CALL_RESULT";
    public const string StateSnapshot = "STATE_SNAPSHOT";
    public const string StateDelta = "STATE_DELTA";
    public const string MessagesSnapshot = "MESSAGES_SNAPSHOT";
    public const string Raw = "RAW";
    public const string Custom = "CUSTOM";
}
```

- [ ] **Step 4: Create AG-UI event records**

```csharp
// src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs
using System.Text.Json.Serialization;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Base type for all AG-UI protocol events. Serialized as SSE <c>data:</c> frames.
/// The <c>Type</c> property serves as the event discriminator on the wire.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStartedEvent), AgUiEventType.RunStarted)]
[JsonDerivedType(typeof(RunFinishedEvent), AgUiEventType.RunFinished)]
[JsonDerivedType(typeof(RunErrorEvent), AgUiEventType.RunError)]
[JsonDerivedType(typeof(TextMessageStartEvent), AgUiEventType.TextMessageStart)]
[JsonDerivedType(typeof(TextMessageContentEvent), AgUiEventType.TextMessageContent)]
[JsonDerivedType(typeof(TextMessageEndEvent), AgUiEventType.TextMessageEnd)]
public abstract record AgUiEvent
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

/// <summary>Signals the start of an agent run.</summary>
public sealed record RunStartedEvent(
    [property: JsonPropertyName("threadId")] string ThreadId,
    [property: JsonPropertyName("runId")] string RunId
) : AgUiEvent
{
    [JsonPropertyName("type")]
    public override string Type => AgUiEventType.RunStarted;
}

/// <summary>Signals successful completion of an agent run.</summary>
public sealed record RunFinishedEvent(
    [property: JsonPropertyName("threadId")] string ThreadId,
    [property: JsonPropertyName("runId")] string RunId
) : AgUiEvent
{
    [JsonPropertyName("type")]
    public override string Type => AgUiEventType.RunFinished;
}

/// <summary>Signals an error during an agent run.</summary>
public sealed record RunErrorEvent(
    [property: JsonPropertyName("message")] string Message
) : AgUiEvent
{
    [JsonPropertyName("type")]
    public override string Type => AgUiEventType.RunError;
}

/// <summary>Signals the start of a new text message from the agent.</summary>
public sealed record TextMessageStartEvent(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("role")] string Role
) : AgUiEvent
{
    [JsonPropertyName("type")]
    public override string Type => AgUiEventType.TextMessageStart;
}

/// <summary>A streaming text chunk within a message.</summary>
public sealed record TextMessageContentEvent(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("delta")] string Delta
) : AgUiEvent
{
    [JsonPropertyName("type")]
    public override string Type => AgUiEventType.TextMessageContent;
}

/// <summary>Signals the end of a text message.</summary>
public sealed record TextMessageEndEvent(
    [property: JsonPropertyName("messageId")] string MessageId
) : AgUiEvent
{
    [JsonPropertyName("type")]
    public override string Type => AgUiEventType.TextMessageEnd;
}
```

- [ ] **Step 5: Run tests -- expect all pass**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "FullyQualifiedName~AgUiEventSerialization" -v minimal`
Expected: 6 passed, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs \
        src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs \
        src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEventSerializationTests.cs
git commit -m "feat: add AG-UI protocol event model with serialization tests"
```

---

### Task 2: AG-UI SSE Event Writer

**Files:**
- Create: `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventWriter.cs`
- Test: `src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEventWriterTests.cs`

- [ ] **Step 1: Write SSE framing tests**

```csharp
// src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEventWriterTests.cs
using System.Text;
using FluentAssertions;
using Presentation.AgentHub.AgUi;

namespace Presentation.AgentHub.Tests.AgUi;

public class AgUiEventWriterTests
{
    [Fact]
    public async Task WriteAsync_FormatsAsSseDataFrame()
    {
        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);

        await writer.WriteAsync(new RunStartedEvent("t1", "r1"));

        var output = Encoding.UTF8.GetString(stream.ToArray());
        output.Should().StartWith("data: ");
        output.Should().EndWith("\n\n");
    }

    [Fact]
    public async Task WriteAsync_ProducesValidJson()
    {
        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);

        await writer.WriteAsync(new TextMessageContentEvent("m1", "hello"));

        var output = Encoding.UTF8.GetString(stream.ToArray());
        var jsonPart = output.Replace("data: ", "").TrimEnd('\n');

        var parsed = System.Text.Json.JsonDocument.Parse(jsonPart);
        parsed.RootElement.GetProperty("type").GetString().Should().Be("TEXT_MESSAGE_CONTENT");
        parsed.RootElement.GetProperty("messageId").GetString().Should().Be("m1");
        parsed.RootElement.GetProperty("delta").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task WriteAsync_MultipleEvents_WritesSequentialFrames()
    {
        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);

        await writer.WriteAsync(new RunStartedEvent("t1", "r1"));
        await writer.WriteAsync(new TextMessageStartEvent("m1", "assistant"));
        await writer.WriteAsync(new TextMessageContentEvent("m1", "Hi"));
        await writer.WriteAsync(new TextMessageEndEvent("m1"));
        await writer.WriteAsync(new RunFinishedEvent("t1", "r1"));

        var output = Encoding.UTF8.GetString(stream.ToArray());
        var frames = output.Split("data: ", StringSplitOptions.RemoveEmptyEntries);
        frames.Should().HaveCount(5);
    }

    [Fact]
    public async Task WriteAsync_NullOptionalFields_OmittedFromJson()
    {
        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);

        await writer.WriteAsync(new RunErrorEvent("fail"));

        var output = Encoding.UTF8.GetString(stream.ToArray());
        output.Should().NotContain("threadId");
        output.Should().NotContain("runId");
    }
}
```

- [ ] **Step 2: Run tests -- expect compile failure**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "FullyQualifiedName~AgUiEventWriter" --no-build 2>&1 | head -5`
Expected: Build failure -- `AgUiEventWriter` not found.

- [ ] **Step 3: Implement AgUiEventWriter**

```csharp
// src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventWriter.cs
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Writes AG-UI events as Server-Sent Events frames to a response stream.
/// Each event is serialized as <c>data: {json}\n\n</c>.
/// </summary>
public interface IAgUiEventWriter
{
    /// <summary>Writes a single AG-UI event as an SSE data frame and flushes.</summary>
    Task WriteAsync(AgUiEvent evt, CancellationToken ct = default);
}

/// <summary>
/// SSE event writer that serializes <see cref="AgUiEvent"/> records to the
/// AG-UI wire format: <c>data: {json}\n\n</c> with camelCase property names.
/// </summary>
public sealed class AgUiEventWriter : IAgUiEventWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stream _stream;

    public AgUiEventWriter(Stream responseBody)
    {
        _stream = responseBody;
    }

    public async Task WriteAsync(AgUiEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt, evt.GetType(), SerializerOptions);
        var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await _stream.WriteAsync(frame, ct);
        await _stream.FlushAsync(ct);
    }
}
```

- [ ] **Step 4: Run tests -- expect all pass**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "FullyQualifiedName~AgUiEventWriter" -v minimal`
Expected: 4 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventWriter.cs \
        src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEventWriterTests.cs
git commit -m "feat: add AG-UI SSE event writer with framing tests"
```

---

### Task 3: AG-UI Request Model

**Files:**
- Create: `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiModels.cs`

- [ ] **Step 1: Create request DTOs**

```csharp
// src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiModels.cs
using System.Text.Json;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// AG-UI protocol run request. Matches the <c>RunAgentInput</c> shape from <c>@ag-ui/core</c>.
/// The server uses <see cref="ThreadId"/> to load the persisted conversation and extracts
/// only the latest user message from <see cref="Messages"/>. Other fields are accepted
/// for wire compatibility but not used (server is authoritative for tools, state, and history).
/// </summary>
public sealed record RunAgentInput
{
    public required string ThreadId { get; init; }
    public required string RunId { get; init; }
    public string? ParentRunId { get; init; }
    public required IReadOnlyList<AgUiMessage> Messages { get; init; }

    // Accepted for protocol compliance; server does not use these.
    public JsonElement? State { get; init; }
    public JsonElement? Tools { get; init; }
    public JsonElement? Context { get; init; }
    public JsonElement? ForwardedProps { get; init; }
}

/// <summary>
/// A message in the AG-UI protocol. Maps to the <c>Message</c> union type
/// from <c>@ag-ui/core</c>, but only <c>Id</c>, <c>Role</c>, and <c>Content</c>
/// are used by the server.
/// </summary>
public sealed record AgUiMessage
{
    public required string Id { get; init; }
    public required string Role { get; init; }
    public string? Content { get; init; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Content/Presentation/Presentation.AgentHub/ --no-restore -v quiet`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiModels.cs
git commit -m "feat: add AG-UI RunAgentInput request model"
```

---

### Task 4: AG-UI Run Handler

**Files:**
- Create: `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs`
- Test: `src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiRunHandlerTests.cs`

**Dependencies to understand before implementing:**
- `IConversationStore` -- loads/saves `ConversationRecord` by conversation ID. Located in `Presentation.AgentHub.Interfaces`.
- `ConversationLockRegistry` -- returns a `SemaphoreSlim` per conversation ID to prevent concurrent turns. Singleton in `Presentation.AgentHub.Services`.
- `ExecuteAgentTurnCommand` -- MediatR command in `Application.Core.CQRS.Agents.ExecuteAgentTurn`. Takes `ConversationId`, `AgentName`, `UserMessage`. Returns `AgentTurnResult` with `Response` string.
- `ConversationRecord` -- has `AgentName`, `Messages`, `UserId` properties.
- `ConversationMessage` -- has `Id`, `Role`, `Content`, `Timestamp` properties.
- `MessageRole` -- enum with `User`, `Assistant` values.

Read these types in the codebase before implementing to verify exact signatures.

- [ ] **Step 1: Write handler tests**

```csharp
// src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiRunHandlerTests.cs
using System.Security.Claims;
using System.Text;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using FluentAssertions;
using MediatR;
using Moq;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Services;

namespace Presentation.AgentHub.Tests.AgUi;

public class AgUiRunHandlerTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IConversationStore> _conversationStore = new();
    private readonly ConversationLockRegistry _lockRegistry = new();

    private AgUiRunHandler CreateHandler() =>
        new(_mediator.Object, _conversationStore.Object, _lockRegistry);

    private static ClaimsPrincipal CreateUser(string userId = "user-1") =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId)],
            "test"));

    private static RunAgentInput CreateInput(
        string threadId = "conv-1",
        string userMessage = "Hello") => new()
    {
        ThreadId = threadId,
        RunId = Guid.NewGuid().ToString(),
        Messages = [new AgUiMessage { Id = Guid.NewGuid().ToString(), Role = "user", Content = userMessage }],
    };

    [Fact]
    public async Task HandleRunAsync_ConversationNotFound_EmitsRunError()
    {
        _conversationStore
            .Setup(s => s.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationRecord?)null);

        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);
        var handler = CreateHandler();

        await handler.HandleRunAsync(CreateInput(), writer, CreateUser());

        var output = Encoding.UTF8.GetString(stream.ToArray());
        output.Should().Contain("RUN_STARTED");
        output.Should().Contain("RUN_ERROR");
        output.Should().Contain("not found");
        output.Should().NotContain("RUN_FINISHED");
    }

    [Fact]
    public async Task HandleRunAsync_WrongUser_EmitsRunError()
    {
        var conversation = CreateConversationRecord("conv-1", "agent-1", "other-user");
        _conversationStore
            .Setup(s => s.LoadAsync("conv-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(conversation);

        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);
        var handler = CreateHandler();

        await handler.HandleRunAsync(CreateInput(), writer, CreateUser("user-1"));

        var output = Encoding.UTF8.GetString(stream.ToArray());
        output.Should().Contain("RUN_ERROR");
        output.Should().Contain("not authorized");
    }

    [Fact]
    public async Task HandleRunAsync_HappyPath_EmitsFullEventSequence()
    {
        var conversation = CreateConversationRecord("conv-1", "agent-1", "user-1");
        _conversationStore
            .Setup(s => s.LoadAsync("conv-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(conversation);

        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentTurnResult.Succeeded("Hello, how can I help?"));

        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);
        var handler = CreateHandler();

        await handler.HandleRunAsync(CreateInput(), writer, CreateUser());

        var output = Encoding.UTF8.GetString(stream.ToArray());
        var frames = output.Split("data: ", StringSplitOptions.RemoveEmptyEntries);

        // Expect: RUN_STARTED, TEXT_MESSAGE_START, 1+ TEXT_MESSAGE_CONTENT, TEXT_MESSAGE_END, RUN_FINISHED
        frames.Should().HaveCountGreaterOrEqualTo(5);
        output.Should().Contain("RUN_STARTED");
        output.Should().Contain("TEXT_MESSAGE_START");
        output.Should().Contain("TEXT_MESSAGE_CONTENT");
        output.Should().Contain("TEXT_MESSAGE_END");
        output.Should().Contain("RUN_FINISHED");
        output.Should().NotContain("RUN_ERROR");
    }

    [Fact]
    public async Task HandleRunAsync_AgentFails_EmitsRunError()
    {
        var conversation = CreateConversationRecord("conv-1", "agent-1", "user-1");
        _conversationStore
            .Setup(s => s.LoadAsync("conv-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(conversation);

        _mediator
            .Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentTurnResult.Failed("Model returned an error"));

        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);
        var handler = CreateHandler();

        await handler.HandleRunAsync(CreateInput(), writer, CreateUser());

        var output = Encoding.UTF8.GetString(stream.ToArray());
        output.Should().Contain("RUN_STARTED");
        output.Should().Contain("RUN_ERROR");
        output.Should().NotContain("TEXT_MESSAGE_START");
    }

    // Helper -- construct a minimal ConversationRecord.
    // Adjust constructor args to match the actual ConversationRecord shape in the codebase.
    private static ConversationRecord CreateConversationRecord(
        string id, string agentName, string userId) =>
        new(id, agentName, userId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], null, null);
}
```

**Important:** The test references `ConversationRecord`, `AgentTurnResult`, and `ExecuteAgentTurnCommand`. Before running, verify these types' exact constructors and factory methods by reading:
- `src/Content/Presentation/Presentation.AgentHub/Interfaces/IConversationStore.cs` for `ConversationRecord`
- `src/Content/Application/Application.Core/CQRS/Agents/ExecuteAgentTurn/` for `ExecuteAgentTurnCommand` and `AgentTurnResult`

Adjust the test helper `CreateConversationRecord` and `AgentTurnResult.Succeeded()`/`.Failed()` calls to match the actual types.

- [ ] **Step 2: Run tests -- expect compile failure**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "FullyQualifiedName~AgUiRunHandler" --no-build 2>&1 | head -5`
Expected: Build failure -- `AgUiRunHandler` not found.

- [ ] **Step 3: Implement AgUiRunHandler**

```csharp
// src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs
using System.Security.Claims;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using MediatR;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Services;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Orchestrates an AG-UI agent run: validates ownership, acquires a conversation lock,
/// dispatches to MediatR, and emits AG-UI SSE events for the response.
/// </summary>
public sealed class AgUiRunHandler
{
    private const int ChunkSize = 50;

    private readonly IMediator _mediator;
    private readonly IConversationStore _conversationStore;
    private readonly ConversationLockRegistry _lockRegistry;

    public AgUiRunHandler(
        IMediator mediator,
        IConversationStore conversationStore,
        ConversationLockRegistry lockRegistry)
    {
        _mediator = mediator;
        _conversationStore = conversationStore;
        _lockRegistry = lockRegistry;
    }

    public async Task HandleRunAsync(
        RunAgentInput input,
        IAgUiEventWriter writer,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        var runId = input.RunId;
        var threadId = input.ThreadId;

        await writer.WriteAsync(new RunStartedEvent(threadId, runId), ct);

        try
        {
            var conversation = await _conversationStore.LoadAsync(threadId, ct);
            if (conversation is null)
            {
                await writer.WriteAsync(new RunErrorEvent($"Conversation '{threadId}' not found"), ct);
                return;
            }

            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            if (!string.Equals(conversation.UserId, userId, StringComparison.Ordinal))
            {
                await writer.WriteAsync(new RunErrorEvent("User is not authorized for this conversation"), ct);
                return;
            }

            var userMessage = input.Messages
                .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));

            if (userMessage is null || string.IsNullOrWhiteSpace(userMessage.Content))
            {
                await writer.WriteAsync(new RunErrorEvent("No user message found in input"), ct);
                return;
            }

            var semaphore = _lockRegistry.GetOrCreate(threadId);
            await semaphore.WaitAsync(ct);

            try
            {
                await AppendUserMessageAsync(conversation, userMessage, ct);

                var command = new ExecuteAgentTurnCommand
                {
                    ConversationId = threadId,
                    AgentName = conversation.AgentName,
                    UserMessage = userMessage.Content,
                };

                var result = await _mediator.Send(command, ct);

                if (!result.IsSuccess)
                {
                    await writer.WriteAsync(new RunErrorEvent(result.ErrorMessage ?? "Agent turn failed"), ct);
                    return;
                }

                var assistantMessageId = Guid.NewGuid().ToString();
                var response = result.Response ?? string.Empty;

                await writer.WriteAsync(new TextMessageStartEvent(assistantMessageId, "assistant"), ct);
                await StreamChunksAsync(writer, assistantMessageId, response, ct);
                await writer.WriteAsync(new TextMessageEndEvent(assistantMessageId), ct);

                await AppendAssistantMessageAsync(conversation, assistantMessageId, response, ct);

                await writer.WriteAsync(new RunFinishedEvent(threadId, runId), ct);
            }
            finally
            {
                semaphore.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected -- nothing to write
        }
        catch (Exception ex)
        {
            await writer.WriteAsync(new RunErrorEvent(SanitizeError(ex)), ct);
        }
    }

    private static async Task StreamChunksAsync(
        IAgUiEventWriter writer,
        string messageId,
        string fullResponse,
        CancellationToken ct)
    {
        for (var i = 0; i < fullResponse.Length; i += ChunkSize)
        {
            var delta = fullResponse.Substring(i, Math.Min(ChunkSize, fullResponse.Length - i));
            await writer.WriteAsync(new TextMessageContentEvent(messageId, delta), ct);
        }
    }

    private async Task AppendUserMessageAsync(
        ConversationRecord conversation,
        AgUiMessage message,
        CancellationToken ct)
    {
        var userMsg = new ConversationMessage(
            Guid.Parse(message.Id),
            MessageRole.User,
            message.Content!,
            DateTimeOffset.UtcNow,
            null);

        conversation.Messages.Add(userMsg);
        await _conversationStore.SaveAsync(conversation, ct);
    }

    private async Task AppendAssistantMessageAsync(
        ConversationRecord conversation,
        string messageId,
        string content,
        CancellationToken ct)
    {
        var assistantMsg = new ConversationMessage(
            Guid.Parse(messageId),
            MessageRole.Assistant,
            content,
            DateTimeOffset.UtcNow,
            null);

        conversation.Messages.Add(assistantMsg);
        await _conversationStore.SaveAsync(conversation, ct);
    }

    private static string SanitizeError(Exception ex) =>
        ex is TimeoutException
            ? "Agent turn timed out"
            : "An unexpected error occurred during the agent turn";
}
```

**Important:** The handler references `ConversationRecord`, `ConversationMessage`, `MessageRole`, `ExecuteAgentTurnCommand`, and `AgentTurnResult`. Before compiling, read these types in the codebase to verify:
- `ConversationRecord.Messages` -- is it `List<>` (mutable) or `IReadOnlyList<>`? If readonly, the handler needs to create a new record with the appended message instead of mutating.
- `ExecuteAgentTurnCommand` properties -- verify `ConversationId`, `AgentName`, `UserMessage` exist and are the right types.
- `AgentTurnResult` -- verify `IsSuccess`, `Response`, `ErrorMessage` properties exist. Adjust if the type uses `Result<T>` pattern instead.
- `ConversationMessage` constructor -- verify the parameter order and types match.

Adapt the code to match the actual types found.

- [ ] **Step 4: Run tests -- expect all pass**

Run: `dotnet test src/Content/Tests/Presentation.AgentHub.Tests/ --filter "FullyQualifiedName~AgUiRunHandler" -v minimal`
Expected: 4 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs \
        src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiRunHandlerTests.cs
git commit -m "feat: add AG-UI run handler with conversation orchestration"
```

---

### Task 5: AG-UI Endpoint + DI Registration

**Files:**
- Create: `src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEndpoints.cs`
- Modify: `src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs:177` (add handler registration)
- Modify: `src/Content/Presentation/Presentation.AgentHub/Program.cs:37` (map AG-UI endpoints)

- [ ] **Step 1: Create endpoint registration**

```csharp
// src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEndpoints.cs
namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Maps the AG-UI protocol SSE endpoint. The endpoint accepts an AG-UI
/// <see cref="RunAgentInput"/> via HTTP POST and streams the agent response
/// as Server-Sent Events using the AG-UI event format.
/// </summary>
public static class AgUiEndpoints
{
    /// <summary>
    /// Maps <c>POST /ag-ui/run</c> with authorization required.
    /// Responses use <c>text/event-stream</c> content type.
    /// </summary>
    public static IEndpointRouteBuilder MapAgUiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/ag-ui/run", HandleRunAsync)
            .RequireAuthorization()
            .Accepts<RunAgentInput>("application/json")
            .WithName("AgUiRun")
            .WithDescription("AG-UI protocol streaming endpoint");

        return endpoints;
    }

    private static async Task HandleRunAsync(
        HttpContext httpContext,
        RunAgentInput input,
        AgUiRunHandler handler,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.ThreadId) ||
            string.IsNullOrWhiteSpace(input.RunId) ||
            input.Messages is not { Count: > 0 })
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(
                new { error = "threadId, runId, and at least one message are required" }, ct);
            return;
        }

        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        var writer = new AgUiEventWriter(httpContext.Response.Body);
        await handler.HandleRunAsync(input, writer, httpContext.User, ct);
    }
}
```

- [ ] **Step 2: Register AgUiRunHandler in DependencyInjection.cs**

In `src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs`, add after the `ConversationLockRegistry` registration (around line 180):

```csharp
// AG-UI protocol handler — scoped because it takes a per-request
// ClaimsPrincipal and writes to the per-request response stream.
services.AddScoped<AgUi.AgUiRunHandler>();
```

Add the using at the top of the file if not already present:
```csharp
using Presentation.AgentHub.AgUi;
```

- [ ] **Step 3: Map AG-UI endpoints in Program.cs**

In `src/Content/Presentation/Presentation.AgentHub/Program.cs`, add after `app.MapHub<AgentTelemetryHub>("/hubs/agent");` (line 37):

```csharp
app.MapAgUiEndpoints();
```

Add the using at the top:
```csharp
using Presentation.AgentHub.AgUi;
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/AgenticHarness.slnx --no-restore -v quiet`
Expected: Build succeeded. If there are type mismatches from Task 4, fix them now.

- [ ] **Step 5: Run all existing tests to verify no regressions**

Run: `dotnet test src/AgenticHarness.slnx -v minimal`
Expected: All tests pass. No regressions.

- [ ] **Step 6: Commit**

```bash
git add src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEndpoints.cs \
        src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs \
        src/Content/Presentation/Presentation.AgentHub/Program.cs
git commit -m "feat: register AG-UI endpoint and handler in DI pipeline"
```

---

### Task 6: Frontend AG-UI Client Setup

**Files:**
- Modify: `src/Content/Presentation/Presentation.WebUI/package.json`
- Create: `src/Content/Presentation/Presentation.WebUI/src/lib/agUiClient.ts`

- [ ] **Step 1: Install AG-UI packages**

Run from the WebUI directory:

```bash
cd src/Content/Presentation/Presentation.WebUI
npm install @ag-ui/client@^0.0.53 @ag-ui/core@^0.0.53 rxjs@^7
```

Expected: 3 packages added, `package.json` and `package-lock.json` updated.

- [ ] **Step 2: Verify build**

```bash
cd src/Content/Presentation/Presentation.WebUI
npm run build
```

Expected: Build succeeds with no errors.

- [ ] **Step 3: Create AG-UI client factory**

```typescript
// src/Content/Presentation/Presentation.WebUI/src/lib/agUiClient.ts
import { HttpAgent } from '@ag-ui/client';

const AG_UI_BASE_URL = '/ag-ui/run';

export function createAgUiAgent(getAccessToken: () => Promise<string>): HttpAgent {
  return new HttpAgent({
    url: AG_UI_BASE_URL,
  });
}

export async function buildAgUiHeaders(
  getAccessToken: () => Promise<string>,
): Promise<Record<string, string>> {
  try {
    const token = await getAccessToken();
    if (token) {
      return { Authorization: `Bearer ${token}` };
    }
  } catch {
    // Auth disabled in dev — no token needed
  }
  return {};
}
```

- [ ] **Step 4: Verify build**

```bash
cd src/Content/Presentation/Presentation.WebUI
npm run build
```

Expected: Build succeeds. TypeScript compiles without errors.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Presentation/Presentation.WebUI/package.json \
        src/Content/Presentation/Presentation.WebUI/package-lock.json \
        src/Content/Presentation/Presentation.WebUI/src/lib/agUiClient.ts
git commit -m "feat: add AG-UI client packages and factory"
```

---

### Task 7: AG-UI Chat Hook + Component Wiring

**Files:**
- Create: `src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentStream.ts`
- Modify: `src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.tsx` (remove `sendMessage`)
- Modify: `src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatPanel.tsx` (use AG-UI for sending)

**Before starting:** Read `ChatPanel.tsx` to understand how it currently calls `sendMessage` from `useAgentHub`. The hook change must match the component's call signature.

- [ ] **Step 1: Create the AG-UI streaming hook**

```typescript
// src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentStream.ts
import { useCallback, useRef } from 'react';
import { HttpAgent } from '@ag-ui/client';
import { EventType, type BaseEvent } from '@ag-ui/core';
import type { Subscription } from 'rxjs';
import { useMsal } from '@azure/msal-react';
import { loginRequest } from '@/lib/authConfig';
import { IS_AUTH_DISABLED } from '@/lib/devAuth';
import { useChatStore } from '@/stores/chatStore';

const AG_UI_URL = '/ag-ui/run';

export interface UseAgentStreamReturn {
  sendMessage: (conversationId: string, userMessageId: string, message: string) => void;
  abort: () => void;
}

export function useAgentStream(): UseAgentStreamReturn {
  const { instance } = useMsal();
  const subscriptionRef = useRef<Subscription | null>(null);
  const currentMessageIdRef = useRef<string | undefined>();

  const getHeaders = useCallback(async (): Promise<Record<string, string>> => {
    if (IS_AUTH_DISABLED) return {};
    try {
      const account = instance.getAllAccounts()[0];
      if (!account) return {};
      const result = await instance.acquireTokenSilent({
        account,
        scopes: loginRequest.scopes,
      });
      return { Authorization: `Bearer ${result.accessToken}` };
    } catch {
      return {};
    }
  }, [instance]);

  const abort = useCallback(() => {
    subscriptionRef.current?.unsubscribe();
    subscriptionRef.current = null;
  }, []);

  const sendMessage = useCallback(
    (conversationId: string, userMessageId: string, message: string) => {
      abort();

      const store = useChatStore.getState();
      store.startStreaming();
      currentMessageIdRef.current = undefined;

      (async () => {
        const headers = await getHeaders();

        const agent = new HttpAgent({ url: AG_UI_URL, headers });

        const subscription = agent
          .run({
            threadId: conversationId,
            runId: crypto.randomUUID(),
            messages: [{ id: userMessageId, role: 'user' as const, content: message }],
            tools: [],
            context: [],
            state: {},
            forwardedProps: {},
          })
          .subscribe({
            next: (event: BaseEvent) => {
              const chatStore = useChatStore.getState();

              switch (event.type) {
                case EventType.TEXT_MESSAGE_START:
                  currentMessageIdRef.current = (event as BaseEvent & { messageId: string }).messageId;
                  break;

                case EventType.TEXT_MESSAGE_CONTENT:
                  chatStore.appendToken((event as BaseEvent & { delta: string }).delta);
                  break;

                case EventType.TEXT_MESSAGE_END:
                  chatStore.finalizeStream(
                    chatStore.streamingContent,
                    currentMessageIdRef.current,
                  );
                  currentMessageIdRef.current = undefined;
                  break;

                case EventType.RUN_ERROR:
                  chatStore.setError(
                    (event as BaseEvent & { message?: string }).message ?? 'Agent run failed',
                  );
                  break;
              }
            },
            error: (err: Error) => {
              useChatStore.getState().setError(err.message || 'Connection to agent failed');
            },
            complete: () => {
              subscriptionRef.current = null;
            },
          });

        subscriptionRef.current = subscription;
      })();
    },
    [getHeaders, abort],
  );

  return { sendMessage, abort };
}
```

**Note on types:** The `@ag-ui/core` event types may use slightly different property access patterns than shown above. After installing the package, check the actual exported types:
- `import { EventType } from '@ag-ui/core'` -- verify this export exists
- Check if events have typed properties or if they're accessed via a generic `BaseEvent` shape

If the package exports typed event subclasses, replace the `as BaseEvent & { ... }` casts with proper typed access.

- [ ] **Step 2: Update useAgentHub to remove sendMessage**

In `src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.tsx`:

Remove `sendMessage` from the `UseAgentHubReturn` interface:

```typescript
export interface UseAgentHubReturn {
  connectionState: ConnectionState;
  // sendMessage removed -- now handled by useAgentStream
  startConversation: (agentName: string, conversationId: string) => Promise<ServerConversationMessage[]>;
  invokeToolViaAgent: (conversationId: string, toolName: string, args: Record<string, unknown>) => Promise<void>;
  retryFromMessage: (conversationId: string, assistantMessageId: string) => Promise<void>;
  editAndResubmit: (conversationId: string, userMessageId: string, newContent: string) => Promise<void>;
  setConversationSettings: (conversationId: string, settings: ConversationSettingsInput) => Promise<void>;
}
```

Remove the `sendMessage` property from the `value` object:

```typescript
const value: UseAgentHubReturn = {
  connectionState,
  // sendMessage removed
  startConversation: (agentName, conversationId) =>
    hubInvoke<ServerConversationMessage[]>('StartConversation', agentName, conversationId),
  invokeToolViaAgent: (conversationId, toolName, args) =>
    hubInvoke('InvokeToolViaAgent', conversationId, toolName, JSON.stringify(args)),
  retryFromMessage: (conversationId, assistantMessageId) =>
    hubInvoke('RetryFromMessage', conversationId, assistantMessageId),
  editAndResubmit: (conversationId, userMessageId, newContent) =>
    hubInvoke('EditAndResubmit', conversationId, userMessageId, crypto.randomUUID(), newContent),
  setConversationSettings: (conversationId, settings) =>
    hubInvoke('SetConversationSettings', conversationId, settings),
};
```

- [ ] **Step 3: Update ChatPanel to use AG-UI for sendMessage**

Read `src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatPanel.tsx` to find where `sendMessage` from `useAgentHub` is called. Replace that call with the `sendMessage` from `useAgentStream`.

At the top of ChatPanel, add:
```typescript
import { useAgentStream } from '@/hooks/useAgentStream';
```

Inside the component, add:
```typescript
const { sendMessage: sendAgUiMessage } = useAgentStream();
```

Replace the existing `sendMessage(conversationId, userMessageId, message)` call with `sendAgUiMessage(conversationId, userMessageId, message)`.

**Important:** There may be multiple places where `sendMessage` is called (e.g., retry, edit flows). Only replace the primary send path. Retry and edit flows continue using SignalR via `useAgentHub`.

- [ ] **Step 4: Verify frontend build**

```bash
cd src/Content/Presentation/Presentation.WebUI
npm run build
```

Expected: Build succeeds. No TypeScript errors.

- [ ] **Step 5: Commit**

```bash
git add src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentStream.ts \
        src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.tsx \
        src/Content/Presentation/Presentation.WebUI/src/features/chat/ChatPanel.tsx
git commit -m "feat: wire AG-UI streaming hook for primary chat flow"
```

---

### Task 8: End-to-End Verification

**Files:** None (verification only)

- [ ] **Step 1: Build full solution**

```bash
dotnet build src/AgenticHarness.slnx
```

Expected: Build succeeded.

- [ ] **Step 2: Run all backend tests**

```bash
dotnet test src/AgenticHarness.slnx -v minimal
```

Expected: All tests pass, including the new AG-UI tests from Tasks 1-4.

- [ ] **Step 3: Run frontend build**

```bash
cd src/Content/Presentation/Presentation.WebUI
npm run build
```

Expected: Build succeeds. Zero TypeScript errors.

- [ ] **Step 4: Manual E2E verification**

Start the backend and frontend:
```bash
dotnet run --project src/Content/Presentation/Presentation.AgentHub
# In another terminal:
cd src/Content/Presentation/Presentation.WebUI && npm run dev
```

Test sequence:
1. Open the WebUI in a browser
2. Create a new conversation (uses existing REST/SignalR flow)
3. Send a message -- this should now go through AG-UI SSE instead of SignalR
4. Verify tokens stream in and the response renders
5. Open browser DevTools Network tab -- confirm the request goes to `POST /ag-ui/run` and the response is `text/event-stream`
6. Verify SSE events appear as `data: {"type":"RUN_STARTED",...}` etc.
7. Test error case: send a message to a non-existent conversation ID -- should show error

- [ ] **Step 5: Verify SignalR still works for non-streaming operations**

1. Retry a previous message -- should work via SignalR
2. Edit and resubmit -- should work via SignalR
3. Dashboard telemetry -- should still receive SpanReceived events via SignalR

- [ ] **Step 6: Final commit**

If any fixes were needed during verification, commit them:
```bash
git add -u
git commit -m "fix: address issues found during AG-UI E2E verification"
```

---

## Follow-Up Work (Not In This Plan)

These are natural next steps after the AG-UI streaming path is proven:

1. **Migrate retry/edit to AG-UI or REST** -- Move `RetryFromMessage` and `EditAndResubmit` out of SignalR. Either add them as AG-UI operations or expose as REST endpoints that return AG-UI SSE streams.

2. **Tool call events** -- Emit `TOOL_CALL_START`, `TOOL_CALL_ARGS`, `TOOL_CALL_END`, `TOOL_CALL_RESULT` events during agent turns. Requires the agent execution pipeline to surface tool calls individually rather than as a batch result.

3. **Step events** -- Emit `STEP_STARTED`/`STEP_FINISHED` for sub-operations (RAG retrieval, safety evaluation, reranking). Gives the frontend visibility into the agent's internal pipeline.

4. **State synchronization** -- Use `STATE_SNAPSHOT`/`STATE_DELTA` events to sync conversation settings, agent configuration, and other shared state between client and server.

5. **Extract shared orchestration** -- The `AgUiRunHandler` and `AgentTelemetryHub.DispatchTurnAsync` share similar orchestration logic (auth, locking, message persistence, MediatR dispatch). Extract a `ConversationOrchestrator` service used by both transports.

6. **True token streaming** -- Replace the chunk-after-completion pattern with actual streaming from the LLM via `IAsyncEnumerable<string>`. Requires changes to `ExecuteAgentTurnCommandHandler` in the Application layer.

7. **Remove SignalR conversation methods** -- Once all conversation operations are on AG-UI/REST, strip the hub down to telemetry-only. Rename to `TelemetryHub`.
