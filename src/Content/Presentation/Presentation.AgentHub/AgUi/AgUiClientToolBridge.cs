using System.Text.Json;
using Application.AI.Common.Interfaces.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Interfaces;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// AG-UI implementation of <see cref="IClientToolBridge"/>: the "blocking proxy" that turns a
/// server-side tool invocation into a mid-run client round-trip over the live SSE stream.
/// </summary>
/// <remarks>
/// <para>
/// The owning <see cref="AgUiRunHandler"/> stores the run's <see cref="IAgUiEventWriter"/> in an
/// <see cref="IAgUiEventWriterAccessor"/> (an <c>AsyncLocal</c>) before dispatching the agent turn.
/// Because the tool executes deep inside that same async context, this bridge reads the ambient
/// writer to emit <see cref="ToolCallStartEvent"/>/<see cref="ToolCallArgsEvent"/>/<see cref="ToolCallEndEvent"/>
/// frames, then parks on the <see cref="PendingToolCallRegistry"/> until the browser posts the result
/// to <c>POST /ag-ui/tool-result</c> — all within the one run.
/// </para>
/// <para>
/// Registered as a singleton: it holds no per-run state of its own (the writer is ambient and the
/// pending map lives in the singleton registry), so a single instance safely serves concurrent runs.
/// </para>
/// </remarks>
public sealed class AgUiClientToolBridge : IClientToolBridge
{
    private readonly IAgUiEventWriterAccessor _writerAccessor;
    private readonly PendingToolCallRegistry _registry;
    private readonly IOptionsMonitor<AgentHubConfig> _config;
    private readonly IConversationStore _conversationStore;
    private readonly ClientWidgetCatalog _widgetCatalog;
    private readonly ILogger<AgUiClientToolBridge> _logger;

    /// <summary>Initializes a new <see cref="AgUiClientToolBridge"/>.</summary>
    public AgUiClientToolBridge(
        IAgUiEventWriterAccessor writerAccessor,
        PendingToolCallRegistry registry,
        IOptionsMonitor<AgentHubConfig> config,
        IConversationStore conversationStore,
        ClientWidgetCatalog widgetCatalog,
        ILogger<AgUiClientToolBridge> logger)
    {
        _writerAccessor = writerAccessor;
        _registry = registry;
        _config = config;
        _conversationStore = conversationStore;
        _widgetCatalog = widgetCatalog;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsClientAttached => _writerAccessor.Writer is not null;

    /// <inheritdoc />
    public async Task<string> InvokeAsync(
        string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var writer = _writerAccessor.Writer
            ?? throw new InvalidOperationException(
                "No AG-UI client is attached to the current run; a client round-trip tool cannot be used here.");
        var threadId = _writerAccessor.ThreadId
            ?? throw new InvalidOperationException(
                "The active AG-UI run has no thread id; a client round-trip tool cannot be used here.");

        var callId = Guid.NewGuid().ToString("N");
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _config.CurrentValue.ClientToolTimeoutSeconds));

        // Register (bound to the owning thread) before emitting events so a result can never race
        // ahead of the pending entry, and so only a caller who owns this thread can complete it.
        var resultTask = _registry.RegisterAsync(callId, threadId, timeout, cancellationToken);

        await writer.WriteAsync(new ToolCallStartEvent(callId, toolName), cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync(new ToolCallArgsEvent(callId, argumentsJson ?? "{}"), cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync(new ToolCallEndEvent(callId), cancellationToken).ConfigureAwait(false);

        var result = await resultTask.ConfigureAwait(false);

        // Persist the widget as an assistant message so it re-renders on reload (the live in-browser
        // message is not otherwise persisted). Only after the client posts its result — a timed-out or
        // disconnected round-trip that never displayed the widget throws above, so we do not leave a
        // phantom that reappears on reload contradicting the agent. This still precedes the run handler's
        // final assistant-text append, so the widget lands in the right position. A persistence failure
        // must not break the turn, so it is logged and swallowed.
        if (_widgetCatalog.IsWidget(toolName))
            await PersistWidgetAsync(threadId, toolName, argumentsJson, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Appends an assistant message carrying the widget spec (empty text, so it renders as the widget
    /// only, matching the live in-browser message). Errors are logged and swallowed — durability is
    /// best-effort and must never fail the turn.
    /// </summary>
    private async Task PersistWidgetAsync(
        string threadId, string toolName, string? argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var widget = new WidgetSpec(toolName, doc.RootElement.Clone());
            var message = new ConversationMessage(
                Guid.NewGuid(), MessageRole.Assistant, string.Empty, DateTimeOffset.UtcNow, Widget: widget);
            await _conversationStore.AppendMessageAsync(threadId, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Failed to persist widget message for tool {ToolName} in conversation {ThreadId}; the widget " +
                "was still displayed live but will not survive a reload.", toolName, threadId);
        }
    }
}
