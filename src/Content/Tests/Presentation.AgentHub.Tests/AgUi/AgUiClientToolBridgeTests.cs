using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Interfaces;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

/// <summary>
/// Unit tests for <see cref="AgUiClientToolBridge"/> — the mid-run blocking proxy. Verifies it emits the
/// <c>TOOL_CALL_START</c>/<c>ARGS</c>/<c>END</c> sequence (sharing one callId), parks until the registry
/// is completed out-of-band, returns the client's result, fails fast when no client is attached, and
/// persists a widget message for widget tools (so the render survives a reload) but not for others.
/// </summary>
public sealed class AgUiClientToolBridgeTests
{
    private static IOptionsMonitor<AgentHubConfig> Options(int timeoutSeconds = 30)
    {
        var mock = new Mock<IOptionsMonitor<AgentHubConfig>>();
        mock.Setup(m => m.CurrentValue).Returns(new AgentHubConfig { ClientToolTimeoutSeconds = timeoutSeconds });
        return mock.Object;
    }

    private static AgUiClientToolBridge Bridge(
        IAgUiEventWriterAccessor accessor,
        PendingToolCallRegistry registry,
        IConversationStore? store = null,
        int timeoutSeconds = 30) =>
        new(accessor, registry, Options(timeoutSeconds), store ?? new RecordingConversationStore(),
            new ClientWidgetCatalog(), NullLogger<AgUiClientToolBridge>.Instance);

    [Fact]
    public async Task InvokeAsync_EmitsToolCallSequence_BlocksUntilCompleted_ThenReturnsResult()
    {
        var writer = new CapturingEventWriter();
        var accessor = new AgUiEventWriterAccessor { Writer = writer, ThreadId = "thread-1" };
        var registry = new PendingToolCallRegistry();
        var bridge = Bridge(accessor, registry);

        var invokeTask = bridge.InvokeAsync("dashboard_control", "{\"operation\":\"navigate\"}");

        // The three events must be emitted before the call resolves, all sharing one callId.
        await WaitForAsync(() => writer.Events.Count >= 3);
        invokeTask.IsCompleted.Should().BeFalse("the bridge must block awaiting the client result");

        var start = writer.Events[0].Should().BeOfType<ToolCallStartEvent>().Subject;
        var args = writer.Events[1].Should().BeOfType<ToolCallArgsEvent>().Subject;
        var end = writer.Events[2].Should().BeOfType<ToolCallEndEvent>().Subject;

        start.ToolCallName.Should().Be("dashboard_control");
        args.Delta.Should().Contain("navigate");
        start.ToolCallId.Should().Be(args.ToolCallId).And.Be(end.ToolCallId);

        // Resume out-of-band, exactly like the resume endpoint completing the registry (same thread).
        registry.TryComplete(start.ToolCallId, "thread-1", "navigated").Should().BeTrue();

        (await invokeTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("navigated");
    }

    [Fact]
    public async Task InvokeAsync_NoClientAttached_Throws()
    {
        var accessor = new AgUiEventWriterAccessor { Writer = null };
        var bridge = Bridge(accessor, new PendingToolCallRegistry());

        bridge.IsClientAttached.Should().BeFalse();

        var act = async () => await bridge.InvokeAsync("dashboard_control", "{}");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void IsClientAttached_ReflectsAmbientWriter()
    {
        var accessor = new AgUiEventWriterAccessor();
        var bridge = Bridge(accessor, new PendingToolCallRegistry());

        bridge.IsClientAttached.Should().BeFalse();
        accessor.Writer = new CapturingEventWriter();
        bridge.IsClientAttached.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_WidgetTool_PersistsWidgetMessage()
    {
        var accessor = new AgUiEventWriterAccessor { Writer = new CapturingEventWriter(), ThreadId = "thread-w" };
        var registry = new PendingToolCallRegistry();
        var store = new RecordingConversationStore();
        var bridge = Bridge(accessor, registry, store);

        var invokeTask = bridge.InvokeAsync("render_table", "{\"columns\":[\"Name\"],\"rows\":[[\"Ada\"]]}");

        // Persistence must wait for the client to confirm the render: while the call is still parked the
        // widget is NOT yet persisted, so a timed-out/never-rendered round-trip leaves no phantom.
        await WaitForAsync(() => ((CapturingEventWriter)accessor.Writer!).Events.Count >= 3);
        store.Appended.Should().BeEmpty("the widget is persisted only after the client acknowledges the render");

        // Complete the round-trip as the client posting its result; only now is the widget persisted.
        var callId = ((ToolCallStartEvent)((CapturingEventWriter)accessor.Writer!).Events[0]).ToolCallId;
        registry.TryComplete(callId, "thread-w", "ok").Should().BeTrue();
        await invokeTask.WaitAsync(TimeSpan.FromSeconds(5));

        store.Appended.Should().HaveCount(1);
        var (conversationId, message) = store.Appended[0];
        conversationId.Should().Be("thread-w");
        message.Role.Should().Be(MessageRole.Assistant);
        message.Content.Should().BeEmpty("the widget message renders as the widget only, matching the live message");
        message.Widget.Should().NotBeNull();
        message.Widget!.Type.Should().Be("render_table");
        message.Widget.Args.GetProperty("columns").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_WidgetTool_TimesOut_DoesNotPersist()
    {
        var accessor = new AgUiEventWriterAccessor { Writer = new CapturingEventWriter(), ThreadId = "thread-t" };
        var registry = new PendingToolCallRegistry();
        var store = new RecordingConversationStore();
        var bridge = Bridge(accessor, registry, store, timeoutSeconds: 1);

        // Never complete the registry: the parked call times out and the widget must NOT be persisted,
        // so a round-trip that never rendered leaves no phantom widget on reload.
        var act = async () => await bridge.InvokeAsync("render_table", "{\"columns\":[\"Name\"]}");
        await act.Should().ThrowAsync<TimeoutException>();

        store.Appended.Should().BeEmpty("a timed-out render must not persist a widget");
    }

    [Fact]
    public async Task InvokeAsync_NonWidgetTool_DoesNotPersist()
    {
        var accessor = new AgUiEventWriterAccessor { Writer = new CapturingEventWriter(), ThreadId = "thread-n" };
        var registry = new PendingToolCallRegistry();
        var store = new RecordingConversationStore();
        var bridge = Bridge(accessor, registry, store);

        var invokeTask = bridge.InvokeAsync("dashboard_control", "{\"operation\":\"refresh_data\"}");

        await WaitForAsync(() => ((CapturingEventWriter)accessor.Writer!).Events.Count >= 3);
        store.Appended.Should().BeEmpty("only widget tools are persisted for reload");

        var callId = ((ToolCallStartEvent)((CapturingEventWriter)accessor.Writer!).Events[0]).ToolCallId;
        registry.TryComplete(callId, "thread-n", "ok").Should().BeTrue();
        await invokeTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        condition().Should().BeTrue("the expected state was not reached within the timeout");
    }

    /// <summary>An <see cref="IAgUiEventWriter"/> that records every event written, in order.</summary>
    private sealed class CapturingEventWriter : IAgUiEventWriter
    {
        private readonly List<AgUiEvent> _events = [];
        public IReadOnlyList<AgUiEvent> Events => _events;

        public Task WriteAsync(AgUiEvent evt, CancellationToken ct = default)
        {
            _events.Add(evt);
            return Task.CompletedTask;
        }
    }

    /// <summary>An <see cref="IConversationStore"/> that records appended messages; other members are unused.</summary>
    private sealed class RecordingConversationStore : IConversationStore
    {
        private readonly List<(string ConversationId, ConversationMessage Message)> _appended = [];
        public IReadOnlyList<(string ConversationId, ConversationMessage Message)> Appended => _appended;

        public Task AppendMessageAsync(string conversationId, ConversationMessage message, CancellationToken ct = default)
        {
            _appended.Add((conversationId, message));
            return Task.CompletedTask;
        }

        public Task<ConversationRecord?> GetAsync(string conversationId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationRecord>> ListAsync(string userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ConversationRecord> CreateAsync(string agentName, string userId, string? conversationId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string conversationId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationMessage>?> GetHistoryForDispatch(string conversationId, int maxMessages, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ConversationRecord?> TruncateFromMessageAsync(string conversationId, Guid messageId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ConversationRecord?> UpdateSettingsAsync(string conversationId, ConversationSettings settings, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ConversationRecord?> UpdateTelemetryAsync(string conversationId, Guid observabilitySessionId, TelemetryAccumulator telemetry, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
