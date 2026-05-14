diff --git a/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs b/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs
index 03d539d..ffd738f 100644
--- a/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEventType.cs
@@ -58,4 +58,13 @@ public static class AgUiEventType
 
     /// <summary>A custom, application-defined event type.</summary>
     public const string Custom = "CUSTOM";
+
+    /// <summary>Signals that an agent action requires human approval.</summary>
+    public const string EscalationRequested = "ESCALATION_REQUESTED";
+
+    /// <summary>Signals that a pending escalation has been resolved.</summary>
+    public const string EscalationResolved = "ESCALATION_RESOLVED";
+
+    /// <summary>Warns that a pending escalation is approaching its timeout deadline.</summary>
+    public const string EscalationExpiring = "ESCALATION_EXPIRING";
 }
diff --git a/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs b/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs
index 6860883..869e596 100644
--- a/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiEvents.cs
@@ -18,6 +18,9 @@ namespace Presentation.AgentHub.AgUi;
 [JsonDerivedType(typeof(TextMessageStartEvent), AgUiEventType.TextMessageStart)]
 [JsonDerivedType(typeof(TextMessageContentEvent), AgUiEventType.TextMessageContent)]
 [JsonDerivedType(typeof(TextMessageEndEvent), AgUiEventType.TextMessageEnd)]
+[JsonDerivedType(typeof(EscalationRequestedEvent), AgUiEventType.EscalationRequested)]
+[JsonDerivedType(typeof(EscalationResolvedEvent), AgUiEventType.EscalationResolved)]
+[JsonDerivedType(typeof(EscalationExpiringEvent), AgUiEventType.EscalationExpiring)]
 public abstract record AgUiEvent;
 
 /// <summary>
@@ -84,3 +87,101 @@ public sealed record TextMessageEndEvent(
     /// <summary>The message that has finished streaming.</summary>
     [property: JsonPropertyName("messageId")] string MessageId
 ) : AgUiEvent;
+
+/// <summary>
+/// Signals that an agent action requires human approval. Emitted when the governance
+/// pipeline blocks a tool call and creates an escalation request.
+/// </summary>
+public sealed record EscalationRequestedEvent : AgUiEvent
+{
+    /// <summary>Unique identifier for this escalation.</summary>
+    [JsonPropertyName("escalationId")]
+    public required string EscalationId { get; init; }
+
+    /// <summary>The agent that attempted the action.</summary>
+    [JsonPropertyName("agentId")]
+    public required string AgentId { get; init; }
+
+    /// <summary>The tool or operation the agent tried to invoke.</summary>
+    [JsonPropertyName("toolName")]
+    public required string ToolName { get; init; }
+
+    /// <summary>Human-readable summary of the attempted action.</summary>
+    [JsonPropertyName("description")]
+    public required string Description { get; init; }
+
+    /// <summary>Urgency level (e.g. "Informational", "Blocking", "Critical").</summary>
+    [JsonPropertyName("priority")]
+    public required string Priority { get; init; }
+
+    /// <summary>Ordered list of approver identifiers.</summary>
+    [JsonPropertyName("approvers")]
+    public required IReadOnlyList<string> Approvers { get; init; }
+
+    /// <summary>Seconds before this escalation expires.</summary>
+    [JsonPropertyName("timeoutSeconds")]
+    public required int TimeoutSeconds { get; init; }
+
+    /// <summary>Tool arguments (sanitized for display). Null when omitted.</summary>
+    [JsonPropertyName("arguments")]
+    public IReadOnlyDictionary<string, object>? Arguments { get; init; }
+}
+
+/// <summary>
+/// Signals that a pending escalation has been resolved (approved, denied, timed out, or escalated).
+/// </summary>
+public sealed record EscalationResolvedEvent : AgUiEvent
+{
+    /// <summary>Correlates back to the originating escalation request.</summary>
+    [JsonPropertyName("escalationId")]
+    public required string EscalationId { get; init; }
+
+    /// <summary>Final approval verdict.</summary>
+    [JsonPropertyName("isApproved")]
+    public required bool IsApproved { get; init; }
+
+    /// <summary>How the escalation was resolved (e.g. "Approved", "Denied", "TimedOut").</summary>
+    [JsonPropertyName("resolutionType")]
+    public required string ResolutionType { get; init; }
+
+    /// <summary>When the escalation was resolved.</summary>
+    [JsonPropertyName("resolvedAt")]
+    public required DateTimeOffset ResolvedAt { get; init; }
+
+    /// <summary>Individual approver decisions, if any.</summary>
+    [JsonPropertyName("decisions")]
+    public IReadOnlyList<AgUiApproverDecision>? Decisions { get; init; }
+}
+
+/// <summary>
+/// Lightweight wire-format representation of a single approver's decision.
+/// </summary>
+public sealed record AgUiApproverDecision
+{
+    /// <summary>Identifier of the approver.</summary>
+    [JsonPropertyName("approverName")]
+    public required string ApproverName { get; init; }
+
+    /// <summary>Whether the approver granted approval.</summary>
+    [JsonPropertyName("approved")]
+    public required bool Approved { get; init; }
+
+    /// <summary>Optional reason for the decision.</summary>
+    [JsonPropertyName("reason")]
+    public string? Reason { get; init; }
+}
+
+/// <summary>
+/// Warns that a pending escalation is approaching its timeout deadline.
+/// Enables the dashboard to display a countdown or urgency indicator.
+/// </summary>
+public sealed record EscalationExpiringEvent : AgUiEvent
+{
+    /// <summary>Correlates back to the originating escalation request.</summary>
+    [JsonPropertyName("escalationId")]
+    public required string EscalationId { get; init; }
+
+    /// <summary>Seconds remaining before the escalation times out.</summary>
+    [JsonPropertyName("remainingSeconds")]
+    public required int RemainingSeconds { get; init; }
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs b/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs
index b50ac36..ca3fcd1 100644
--- a/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/AgUi/AgUiRunHandler.cs
@@ -29,6 +29,7 @@ public sealed class AgUiRunHandler
     private readonly IConversationStore _conversationStore;
     private readonly IObservabilityStore _observabilityStore;
     private readonly ConversationLockRegistry _lockRegistry;
+    private readonly IAgUiEventWriterAccessor _writerAccessor;
     private readonly ILogger<AgUiRunHandler> _logger;
 
     /// <summary>
@@ -39,12 +40,14 @@ public sealed class AgUiRunHandler
         IConversationStore conversationStore,
         IObservabilityStore observabilityStore,
         ConversationLockRegistry lockRegistry,
+        IAgUiEventWriterAccessor writerAccessor,
         ILogger<AgUiRunHandler> logger)
     {
         _mediator = mediator;
         _conversationStore = conversationStore;
         _observabilityStore = observabilityStore;
         _lockRegistry = lockRegistry;
+        _writerAccessor = writerAccessor;
         _logger = logger;
     }
 
@@ -137,6 +140,7 @@ public sealed class AgUiRunHandler
 
         var semaphore = _lockRegistry.GetOrCreate(input.ThreadId);
         await semaphore.WaitAsync(ct);
+        _writerAccessor.Writer = writer;
         try
         {
             await ExecuteRunAsync(input, writer, record, userMessage.Content, callerId, observabilitySessionId, ct);
@@ -152,6 +156,7 @@ public sealed class AgUiRunHandler
         }
         finally
         {
+            _writerAccessor.Writer = null;
             semaphore.Release();
         }
     }
diff --git a/src/Content/Presentation/Presentation.AgentHub/AgUi/IAgUiEventWriterAccessor.cs b/src/Content/Presentation/Presentation.AgentHub/AgUi/IAgUiEventWriterAccessor.cs
new file mode 100644
index 0000000..5664314
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/AgUi/IAgUiEventWriterAccessor.cs
@@ -0,0 +1,27 @@
+namespace Presentation.AgentHub.AgUi;
+
+/// <summary>
+/// Provides access to the current AG-UI event writer for the active run.
+/// Uses <see cref="AsyncLocal{T}"/> storage so the writer is scoped to the
+/// async execution context of the AG-UI run handler.
+/// </summary>
+public interface IAgUiEventWriterAccessor
+{
+    /// <summary>Gets or sets the current AG-UI event writer. Null when no run is active.</summary>
+    IAgUiEventWriter? Writer { get; set; }
+}
+
+/// <summary>
+/// Default implementation using <see cref="AsyncLocal{T}"/> for execution-context-scoped storage.
+/// </summary>
+public sealed class AgUiEventWriterAccessor : IAgUiEventWriterAccessor
+{
+    private static readonly AsyncLocal<IAgUiEventWriter?> _current = new();
+
+    /// <inheritdoc />
+    public IAgUiEventWriter? Writer
+    {
+        get => _current.Value;
+        set => _current.Value = value;
+    }
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Notifications/AgUiEscalationNotifier.cs b/src/Content/Presentation/Presentation.AgentHub/Notifications/AgUiEscalationNotifier.cs
new file mode 100644
index 0000000..ec7de73
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Notifications/AgUiEscalationNotifier.cs
@@ -0,0 +1,112 @@
+using Application.AI.Common.Interfaces.Escalation;
+using Domain.AI.Escalation;
+using Microsoft.Extensions.Logging;
+using Presentation.AgentHub.AgUi;
+
+namespace Presentation.AgentHub.Notifications;
+
+/// <summary>
+/// AG-UI notification channel for escalation events. Translates domain escalation
+/// records into AG-UI SSE events and writes them to the active run's event stream.
+/// </summary>
+/// <remarks>
+/// If no AG-UI run is active (i.e., the escalation was triggered from the ConsoleUI
+/// or a non-SSE context), the notifier silently skips event emission. This is by design --
+/// escalation notifications also flow through other channels (Slack, Teams).
+/// </remarks>
+public sealed class AgUiEscalationNotifier : IEscalationNotificationChannel
+{
+    private readonly IAgUiEventWriterAccessor _writerAccessor;
+    private readonly ILogger<AgUiEscalationNotifier> _logger;
+
+    /// <summary>
+    /// Initializes a new <see cref="AgUiEscalationNotifier"/>.
+    /// </summary>
+    public AgUiEscalationNotifier(
+        IAgUiEventWriterAccessor writerAccessor,
+        ILogger<AgUiEscalationNotifier> logger)
+    {
+        _writerAccessor = writerAccessor;
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public async Task NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct)
+    {
+        var writer = _writerAccessor.Writer;
+        if (writer is null)
+        {
+            _logger.LogDebug("No AG-UI writer active; skipping escalation-requested event for {EscalationId}.",
+                request.EscalationId);
+            return;
+        }
+
+        var evt = new EscalationRequestedEvent
+        {
+            EscalationId = request.EscalationId.ToString(),
+            AgentId = request.AgentId,
+            ToolName = request.ToolName,
+            Description = request.Description,
+            Priority = request.Priority.ToString(),
+            Approvers = request.Approvers,
+            TimeoutSeconds = request.TimeoutSeconds,
+            Arguments = request.Arguments.Count > 0
+                ? request.Arguments.ToDictionary(kv => kv.Key, kv => (object)kv.Value)
+                : null,
+        };
+
+        await writer.WriteAsync(evt, ct);
+    }
+
+    /// <inheritdoc />
+    public async Task NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct)
+    {
+        var writer = _writerAccessor.Writer;
+        if (writer is null)
+        {
+            _logger.LogDebug("No AG-UI writer active; skipping escalation-resolved event for {EscalationId}.",
+                outcome.EscalationId);
+            return;
+        }
+
+        var decisions = outcome.Decisions.Count > 0
+            ? outcome.Decisions.Select(d => new AgUiApproverDecision
+            {
+                ApproverName = d.ApproverName,
+                Approved = d.Approved,
+                Reason = d.Reason,
+            }).ToList()
+            : null;
+
+        var evt = new EscalationResolvedEvent
+        {
+            EscalationId = outcome.EscalationId.ToString(),
+            IsApproved = outcome.IsApproved,
+            ResolutionType = outcome.ResolutionType.ToString(),
+            ResolvedAt = outcome.ResolvedAt,
+            Decisions = decisions,
+        };
+
+        await writer.WriteAsync(evt, ct);
+    }
+
+    /// <inheritdoc />
+    public async Task NotifyEscalationExpiringAsync(EscalationRequest request, TimeSpan remaining, CancellationToken ct)
+    {
+        var writer = _writerAccessor.Writer;
+        if (writer is null)
+        {
+            _logger.LogDebug("No AG-UI writer active; skipping escalation-expiring event for {EscalationId}.",
+                request.EscalationId);
+            return;
+        }
+
+        var evt = new EscalationExpiringEvent
+        {
+            EscalationId = request.EscalationId.ToString(),
+            RemainingSeconds = (int)remaining.TotalSeconds,
+        };
+
+        await writer.WriteAsync(evt, ct);
+    }
+}
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEscalationEventSerializationTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEscalationEventSerializationTests.cs
new file mode 100644
index 0000000..b2b9943
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiEscalationEventSerializationTests.cs
@@ -0,0 +1,137 @@
+using System.Text.Json;
+using Domain.AI.Escalation;
+using FluentAssertions;
+using Presentation.AgentHub.AgUi;
+using Xunit;
+
+namespace Presentation.AgentHub.Tests.AgUi;
+
+/// <summary>
+/// Serialization tests for escalation-related AG-UI events.
+/// Verifies correct JSON discriminator and property names on the wire.
+/// </summary>
+public class AgUiEscalationEventSerializationTests
+{
+    private static readonly JsonSerializerOptions JsonOptions = new()
+    {
+        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
+        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
+    };
+
+    [Fact]
+    public void EscalationRequestedEvent_Serializes_WithCorrectTypeDiscriminator()
+    {
+        var evt = new EscalationRequestedEvent
+        {
+            EscalationId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
+            AgentId = "research-agent",
+            ToolName = "file_system_write",
+            Description = "Agent attempted to write to protected directory",
+            Priority = "Critical",
+            Approvers = ["admin@company.com", "security@company.com"],
+            TimeoutSeconds = 300,
+        };
+
+        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);
+
+        json.Should().Contain("\"type\":\"ESCALATION_REQUESTED\"");
+        json.Should().Contain("\"escalationId\":\"a1b2c3d4-e5f6-7890-abcd-ef1234567890\"");
+        json.Should().Contain("\"agentId\":\"research-agent\"");
+        json.Should().Contain("\"toolName\":\"file_system_write\"");
+        json.Should().Contain("\"description\":\"Agent attempted to write to protected directory\"");
+        json.Should().Contain("\"priority\":\"Critical\"");
+        json.Should().Contain("\"timeoutSeconds\":300");
+        json.Should().Contain("admin@company.com");
+        json.Should().Contain("security@company.com");
+    }
+
+    [Fact]
+    public void EscalationResolvedEvent_Serializes_WithCorrectTypeDiscriminator()
+    {
+        var resolvedAt = new DateTimeOffset(2026, 5, 8, 14, 30, 0, TimeSpan.Zero);
+        var evt = new EscalationResolvedEvent
+        {
+            EscalationId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
+            IsApproved = true,
+            ResolutionType = "Approved",
+            ResolvedAt = resolvedAt,
+        };
+
+        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);
+
+        json.Should().Contain("\"type\":\"ESCALATION_RESOLVED\"");
+        json.Should().Contain("\"escalationId\":\"a1b2c3d4-e5f6-7890-abcd-ef1234567890\"");
+        json.Should().Contain("\"isApproved\":true");
+        json.Should().Contain("\"resolutionType\":\"Approved\"");
+    }
+
+    [Fact]
+    public void EscalationExpiringEvent_Serializes_WithCorrectTypeDiscriminator()
+    {
+        var evt = new EscalationExpiringEvent
+        {
+            EscalationId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
+            RemainingSeconds = 30,
+        };
+
+        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);
+
+        json.Should().Contain("\"type\":\"ESCALATION_EXPIRING\"");
+        json.Should().Contain("\"escalationId\":\"a1b2c3d4-e5f6-7890-abcd-ef1234567890\"");
+        json.Should().Contain("\"remainingSeconds\":30");
+    }
+
+    [Fact]
+    public void EscalationRequestedEvent_WithNullOptionalFields_OmitsThem()
+    {
+        var evt = new EscalationRequestedEvent
+        {
+            EscalationId = "test-id",
+            AgentId = "agent-1",
+            ToolName = "tool-1",
+            Description = "desc",
+            Priority = "Blocking",
+            Approvers = ["approver@test.com"],
+            TimeoutSeconds = 60,
+            Arguments = null,
+        };
+
+        var json = JsonSerializer.Serialize<AgUiEvent>(evt, JsonOptions);
+
+        json.Should().NotContain("\"arguments\"");
+    }
+
+    [Fact]
+    public void EscalationResolvedEvent_Deserializes_BackToCorrectType()
+    {
+        var resolvedAt = new DateTimeOffset(2026, 5, 8, 14, 30, 0, TimeSpan.Zero);
+        var original = new EscalationResolvedEvent
+        {
+            EscalationId = "round-trip-id",
+            IsApproved = false,
+            ResolutionType = "Denied",
+            ResolvedAt = resolvedAt,
+            Decisions =
+            [
+                new AgUiApproverDecision
+                {
+                    ApproverName = "admin@company.com",
+                    Approved = false,
+                    Reason = "Too risky",
+                },
+            ],
+        };
+
+        var json = JsonSerializer.Serialize<AgUiEvent>(original, JsonOptions);
+        var deserialized = JsonSerializer.Deserialize<AgUiEvent>(json, JsonOptions);
+
+        deserialized.Should().BeOfType<EscalationResolvedEvent>();
+        var result = (EscalationResolvedEvent)deserialized!;
+        result.EscalationId.Should().Be("round-trip-id");
+        result.IsApproved.Should().BeFalse();
+        result.ResolutionType.Should().Be("Denied");
+        result.Decisions.Should().HaveCount(1);
+        result.Decisions![0].ApproverName.Should().Be("admin@company.com");
+        result.Decisions[0].Reason.Should().Be("Too risky");
+    }
+}
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiRunHandlerTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiRunHandlerTests.cs
index e730cac..544caa4 100644
--- a/src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiRunHandlerTests.cs
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/AgUi/AgUiRunHandlerTests.cs
@@ -121,6 +121,7 @@ public sealed class AgUiRunHandlerTests
             store.Object,
             observability.Object,
             new ConversationLockRegistry(),
+            new AgUiEventWriterAccessor(),
             NullLogger<AgUiRunHandler>.Instance);
     }
 
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiEscalationNotifierTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiEscalationNotifierTests.cs
new file mode 100644
index 0000000..7ef05be
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/Notifications/AgUiEscalationNotifierTests.cs
@@ -0,0 +1,156 @@
+using Domain.AI.Escalation;
+using FluentAssertions;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Logging.Abstractions;
+using Moq;
+using Presentation.AgentHub.AgUi;
+using Presentation.AgentHub.Notifications;
+using Xunit;
+
+namespace Presentation.AgentHub.Tests.Notifications;
+
+/// <summary>
+/// Tests for AgUiEscalationNotifier -- verifies correct domain-to-AG-UI
+/// event translation and writer invocation.
+/// </summary>
+public class AgUiEscalationNotifierTests
+{
+    private readonly Mock<IAgUiEventWriterAccessor> _accessorMock = new();
+    private readonly Mock<IAgUiEventWriter> _writerMock = new();
+    private readonly AgUiEscalationNotifier _sut;
+
+    public AgUiEscalationNotifierTests()
+    {
+        _accessorMock.Setup(a => a.Writer).Returns(_writerMock.Object);
+        _writerMock
+            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
+            .Returns(Task.CompletedTask);
+        _sut = new AgUiEscalationNotifier(
+            _accessorMock.Object,
+            NullLogger<AgUiEscalationNotifier>.Instance);
+    }
+
+    [Fact]
+    public async Task NotifyEscalationRequestedAsync_WritesEscalationRequestedEvent()
+    {
+        var request = CreateRequest();
+
+        await _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
+
+        _writerMock.Verify(
+            w => w.WriteAsync(It.Is<EscalationRequestedEvent>(e =>
+                e.EscalationId == request.EscalationId.ToString() &&
+                e.AgentId == "test-agent" &&
+                e.ToolName == "dangerous_tool" &&
+                e.Priority == "Critical"),
+            CancellationToken.None),
+            Times.Once);
+    }
+
+    [Fact]
+    public async Task NotifyEscalationResolvedAsync_WritesEscalationResolvedEvent()
+    {
+        var outcome = new EscalationOutcome
+        {
+            EscalationId = Guid.NewGuid(),
+            IsApproved = true,
+            Decisions =
+            [
+                new ApproverDecision
+                {
+                    ApproverName = "admin",
+                    Approved = true,
+                    Reason = "Looks good",
+                    RespondedAt = DateTimeOffset.UtcNow,
+                },
+            ],
+            ResolutionType = EscalationResolutionType.Approved,
+            ResolvedAt = DateTimeOffset.UtcNow,
+        };
+
+        await _sut.NotifyEscalationResolvedAsync(outcome, CancellationToken.None);
+
+        _writerMock.Verify(
+            w => w.WriteAsync(It.Is<EscalationResolvedEvent>(e =>
+                e.EscalationId == outcome.EscalationId.ToString() &&
+                e.IsApproved == true &&
+                e.ResolutionType == "Approved"),
+            CancellationToken.None),
+            Times.Once);
+    }
+
+    [Fact]
+    public async Task NotifyEscalationExpiringAsync_WritesEscalationExpiringEvent()
+    {
+        var request = CreateRequest();
+        var remaining = TimeSpan.FromSeconds(45);
+
+        await _sut.NotifyEscalationExpiringAsync(request, remaining, CancellationToken.None);
+
+        _writerMock.Verify(
+            w => w.WriteAsync(It.Is<EscalationExpiringEvent>(e =>
+                e.EscalationId == request.EscalationId.ToString() &&
+                e.RemainingSeconds == 45),
+            CancellationToken.None),
+            Times.Once);
+    }
+
+    [Fact]
+    public async Task NotifyEscalationRequestedAsync_CancellationToken_PassedToWriter()
+    {
+        using var cts = new CancellationTokenSource();
+        var request = CreateRequest();
+
+        await _sut.NotifyEscalationRequestedAsync(request, cts.Token);
+
+        _writerMock.Verify(
+            w => w.WriteAsync(It.IsAny<EscalationRequestedEvent>(), cts.Token),
+            Times.Once);
+    }
+
+    [Fact]
+    public async Task NotifyEscalationRequestedAsync_WriterThrows_PropagatesException()
+    {
+        _writerMock
+            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new InvalidOperationException("Stream closed"));
+
+        var request = CreateRequest();
+
+        var act = () => _sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
+
+        await act.Should().ThrowAsync<InvalidOperationException>()
+            .WithMessage("Stream closed");
+    }
+
+    [Fact]
+    public async Task NotifyEscalationRequestedAsync_NoWriter_SilentlyReturns()
+    {
+        _accessorMock.Setup(a => a.Writer).Returns((IAgUiEventWriter?)null);
+        var sut = new AgUiEscalationNotifier(
+            _accessorMock.Object,
+            NullLogger<AgUiEscalationNotifier>.Instance);
+
+        var request = CreateRequest();
+
+        await sut.NotifyEscalationRequestedAsync(request, CancellationToken.None);
+
+        _writerMock.Verify(
+            w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()),
+            Times.Never);
+    }
+
+    private static EscalationRequest CreateRequest() => new()
+    {
+        EscalationId = Guid.NewGuid(),
+        AgentId = "test-agent",
+        ToolName = "dangerous_tool",
+        Arguments = new Dictionary<string, string> { ["path"] = "/etc/config" },
+        Description = "Agent attempted dangerous operation",
+        RiskLevel = RiskLevel.High,
+        Priority = EscalationPriority.Critical,
+        Approvers = ["admin@company.com"],
+        TimeoutSeconds = 300,
+        RequestedAt = DateTimeOffset.UtcNow,
+    };
+}
