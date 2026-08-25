using System.Diagnostics;
using System.Security.Claims;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.Common.Exceptions.ExceptionTypes;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Telemetry.Conventions;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Application.AI.Common.Models.Conversations;
using Presentation.Common.Extensions;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Orchestrates a single AG-UI run: validates ownership, leases the conversation's turn,
/// dispatches to the agent pipeline via MediatR, and emits AG-UI SSE events.
/// </summary>
/// <remarks>
/// This mirrors the logic in <c>AgentTelemetryHub.DispatchTurnAsync</c> but targets
/// the AG-UI SSE protocol instead of SignalR. Register as a scoped service.
/// </remarks>
public sealed class AgUiRunHandler
{
    private const int ChunkSize = 50;

    private readonly IMediator _mediator;
    private readonly IConversationStore _conversationStore;
    private readonly IObservabilityStore _observabilityStore;
    private readonly IConversationTelemetryRecorder _telemetryRecorder;
    private readonly IConversationTurnLease _turnLease;
    private readonly IAgUiEventWriterAccessor _writerAccessor;
    private readonly IConversationBudgetTracker _conversationBudget;
    private readonly IToolCallReplayTreatment _toolCallReplayTreatment;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AgUiRunHandler> _logger;

    /// <summary>
    /// Initializes a new <see cref="AgUiRunHandler"/>.
    /// </summary>
    public AgUiRunHandler(
        IMediator mediator,
        IConversationStore conversationStore,
        IObservabilityStore observabilityStore,
        IConversationTelemetryRecorder telemetryRecorder,
        IConversationTurnLease turnLease,
        IAgUiEventWriterAccessor writerAccessor,
        IConversationBudgetTracker conversationBudget,
        IToolCallReplayTreatment toolCallReplayTreatment,
        IHostEnvironment environment,
        ILogger<AgUiRunHandler> logger)
    {
        _mediator = mediator;
        _conversationStore = conversationStore;
        _observabilityStore = observabilityStore;
        _telemetryRecorder = telemetryRecorder;
        _turnLease = turnLease;
        _writerAccessor = writerAccessor;
        _conversationBudget = conversationBudget;
        _toolCallReplayTreatment = toolCallReplayTreatment;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Handles an AG-UI run request end-to-end.
    /// </summary>
    /// <param name="input">The deserialized <c>RunAgentInput</c> from the request body.</param>
    /// <param name="writer">The SSE event writer targeting the HTTP response stream.</param>
    /// <param name="user">The authenticated user principal from the HTTP context.</param>
    /// <param name="ct">Cancellation token (triggered on client disconnect).</param>
    public async Task HandleRunAsync(
        RunAgentInput input,
        IAgUiEventWriter writer,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        await writer.WriteAsync(new RunStartedEvent(input.ThreadId, input.RunId), ct);

        string callerId;
        try
        {
            callerId = GetCallerId(user);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "AG-UI run rejected — missing identity claim.");
            await writer.WriteAsync(new RunErrorEvent("Unable to determine caller identity."), ct);
            return;
        }

        ConversationRecord? record;
        try
        {
            record = await _conversationStore.GetAsync(input.ThreadId, callerId, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        // Ahead of the general handler on purpose. This stream reports failures as events rather than
        // status codes, so an ownership refusal caught by the catch-all below would reach the client
        // as "an error occurred" — turning a decision the harness made deliberately into what looks
        // like a fault. The store has already logged the caller, thread, and real owner.
        catch (ConversationAccessDeniedException)
        {
            // Not logged again here: the store already recorded the caller, the conversation, and its
            // real owner. A second line adds no fact and doubles every refusal in the audit trail.
            await writer.WriteAsync(new RunErrorEvent("Access denied."), ct);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AG-UI run {RunId}: error loading conversation {ThreadId}.", input.RunId, input.ThreadId);
            await writer.WriteAsync(new RunErrorEvent("An error occurred loading the conversation."), ct);
            return;
        }

        if (record is null)
        {
            _logger.LogWarning("AG-UI run {RunId}: conversation {ThreadId} not found.", input.RunId, input.ThreadId);
            await writer.WriteAsync(new RunErrorEvent("Conversation not found."), ct);
            return;
        }

        var userMessage = input.Messages
            .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));

        if (userMessage is null || string.IsNullOrWhiteSpace(userMessage.Content))
        {
            _logger.LogWarning("AG-UI run {RunId}: no user message found in input.", input.RunId);
            await writer.WriteAsync(new RunErrorEvent("No user message found in the request."), ct);
            return;
        }

        Activity.Current?.AddBaggage(UserConventions.UserId, callerId);

        IConversationTurnLeaseHandle lease;
        try
        {
            // Blocks while another turn on this conversation is in flight — here or in another host.
            lease = await _turnLease.AcquireAsync(input.ThreadId, ct);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected while queued behind the turn ahead of it — no event to emit.
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AG-UI run {RunId}: could not lease a turn on conversation {ThreadId}.",
                input.RunId, input.ThreadId);
            await TryWriteErrorAsync(writer, "The conversation is not available right now.", ct);
            return;
        }

        await using (lease)
        {
            await RunLeasedTurnAsync(input, writer, lease, userMessage, callerId, ct);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs the turn while <paramref name="lease"/> is held: binds the turn to a token that a lost
    /// lease cancels, re-reads the conversation now that the turn is exclusive, dispatches, and
    /// reports each way the turn can end.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="HandleRunAsync"/> because the two answer different questions — that
    /// one decides whether a turn may run at all, this one runs it — and because putting the leased
    /// section in its own method makes the extent of the lease something the reader can see rather
    /// than have to trace.
    /// </remarks>
    private async Task RunLeasedTurnAsync(
        RunAgentInput input,
        IAgUiEventWriter writer,
        IConversationTurnLeaseHandle lease,
        AgUiMessage userMessage,
        string callerId,
        CancellationToken ct)
    {
        // Losing the lease mid-turn has to stop the turn. Another host now holds it, so anything
        // written from here on is the second half of exactly the concurrent turn the lease exists
        // to prevent.
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LeaseLost);

        // Names the agent this run was counted against, and by being non-null says that it was counted
        // at all. The gauge is incremented only once the conversation has been read — that is the first
        // moment the agent name exists — so the finally cannot decrement unconditionally.
        string? countedAgent = null;

        try
        {
            // Re-read now that the turn is exclusive. The record loaded before the lease, and
            // everything read from it — the message count this turn is numbered by, the settings the
            // model is called with — could have been changed by the turn this one just waited behind.
            // That was already true of the semaphore this replaces, but the turn ahead can now belong
            // to another host, so "nothing happened in between" is no longer a safe reading. The
            // SignalR path already re-reads inside its lock.
            var leased = await _conversationStore.GetAsync(input.ThreadId, callerId, turnCts.Token);

            if (leased is null)
            {
                _logger.LogWarning(
                    "AG-UI run {RunId}: conversation {ThreadId} was deleted while this turn queued.",
                    input.RunId, input.ThreadId);
                await writer.WriteAsync(new RunErrorEvent("Conversation not found."), ct);
                return;
            }

            // Telemetry is established from the LEASED record, not the pre-lease snapshot. The turn
            // number and the running totals both come from it, and the turn this one waited behind may
            // have advanced them — numbering from the stale copy would collide with a turn already
            // written.
            var telemetry = await _telemetryRecorder.BeginAsync(
                input.ThreadId, callerId, leased.AgentName, leased, turnCts.Token);

            // What this path can honestly count is a run, and it counts every one. It used to increment
            // the shared active-sessions gauge only when a session was opened, and never decrement it —
            // there is no moment here that ends a session, because a stateless request leaves the
            // conversation's open for the next one. So the number it contributed was "conversations
            // this transport has ever started", rising forever, added to two other transports' answers
            // to two other questions (issue #289). A run, by contrast, plainly ends: in the finally.
            countedAgent = leased.AgentName;
            OrchestrationMetrics.RunsActive.Add(
                1, new TagList { { AgentConventions.Name, countedAgent } });

            _writerAccessor.Writer = writer;
            _writerAccessor.ThreadId = input.ThreadId;
            _writerAccessor.CallerId = callerId;
            await ExecuteRunAsync(
                input, writer, leased, userMessage, callerId, telemetry, turnCts.Token);
        }
        catch (OperationCanceledException)
        {
            // A cancelled turn is routine when the client disconnected, and is not routine when the
            // lease was taken — telling them apart is the difference between a silent control and one
            // whose effects can be seen. Both halves of the test matter: when the client has also
            // disconnected, the disconnect is the honest explanation, and there is no longer a stream
            // for the explanation to reach anyway. Same rule as
            // ConversationOrchestrator.WithTurnLeaseAsync, deliberately.
            if (lease.LeaseLost.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "AG-UI run {RunId}: turn stopped because the lease on conversation {ThreadId} was lost.",
                    input.RunId, input.ThreadId);
                await TryWriteErrorAsync(writer, Services.ConversationLeaseNotice.Message, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AG-UI run {RunId}: unhandled error during turn execution.", input.RunId);
            await TryWriteErrorAsync(writer, "An unexpected error occurred.", ct);
        }
        finally
        {
            if (countedAgent is not null)
            {
                OrchestrationMetrics.RunsActive.Add(
                    -1, new TagList { { AgentConventions.Name, countedAgent } });
            }

            _writerAccessor.Writer = null;
            _writerAccessor.ThreadId = null;
            _writerAccessor.CallerId = null;
        }
    }

    private async Task ExecuteRunAsync(
        RunAgentInput input,
        IAgUiEventWriter writer,
        ConversationRecord record,
        AgUiMessage userMessage,
        string callerId,
        ConversationTelemetryState telemetry,
        CancellationToken ct)
    {
        var userMessageText = userMessage.Content!;

        // Persist the user message under the client-supplied id when present so the optimistic
        // UI message and the server record share the same id. Retry/edit operations reference
        // this id, so minting a fresh one here would silently desync the client and break them.
        // Fall back to a server-generated id only when the client omits or sends an invalid id.
        var userMsg = new ConversationMessage(
            ParseClientId(userMessage.Id),
            MessageRole.User,
            userMessageText,
            DateTimeOffset.UtcNow);
        await _conversationStore.AppendMessageAsync(input.ThreadId, callerId, userMsg, ct);

        // Load truncated history for dispatch (mirrors hub's MaxHistoryMessages).
        // Use a reasonable default — the hub reads this from config; we use 50 here
        // since AgUiRunHandler is not wired to AgentHubConfig directly.
        var history = await _conversationStore.GetHistoryForDispatch(input.ThreadId, callerId, 50, ct) ?? [];

        // Counted from the conversation's completed turns, not its message count. Per-turn observability
        // rows are keyed by conversation AND turn number, and the bundle-run path numbers the same
        // conversation from the same counter (issue #255). Message count advances two per exchange, so
        // it produced 1, 3, 5… here against 1, 2, 3… there — two writers interleaving into one key
        // space, overwriting each other's turns on any conversation driven from both.
        var turnNumber = telemetry.NextTurnNumber;

        // Conversation-lifetime budget gate: decline gracefully (no LLM dispatch) when the
        // conversation has exhausted its cumulative ceiling, emitting the explanatory message as a
        // normal assistant turn rather than a RunErrorEvent. No-op when the budget is disabled.
        var budgetStatus = await _conversationBudget.GetStatusAsync(input.ThreadId, ct);
        if (budgetStatus.IsExhausted)
        {
            await EmitBudgetExhaustedAsync(writer, input, record.AgentName, callerId, ct);
            return;
        }

        var command = new ExecuteAgentTurnCommand
        {
            AgentName = record.AgentName,
            UserMessage = userMessageText,
            ConversationHistory = ToMeaiHistory(history),
            ConversationId = input.ThreadId,
            TurnNumber = turnNumber,
            DeploymentOverride = record.Settings?.DeploymentName,
            Temperature = record.Settings?.Temperature,
            SystemPromptOverride = record.Settings?.SystemPromptOverride,
            ObservabilitySessionId = telemetry.SessionId,
        };

        AgentTurnResult result;
        try
        {
            result = await _mediator.Send(command, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AG-UI run {RunId}: MediatR dispatch failed.", input.RunId);
            await writer.WriteAsync(new RunErrorEvent("An error occurred during agent execution."), ct);
            return;
        }

        if (!result.Success)
        {
            // A cancelled turn (e.g. caller disconnect) is routine — abort like the
            // OperationCanceledException catch above instead of emitting a user-facing
            // error event, consistent with the SignalR transport.
            if (result.ErrorKind == AgentTurnErrorKind.Cancelled)
                throw new OperationCanceledException(ct);

            _logger.LogWarning("AG-UI run {RunId}: agent returned failure — {Error}.", input.RunId, result.Error);

            // A provider-configuration failure carries an actionable, secret-free message. Surface it
            // to the developer in Development so the chat itself explains what to fix; keep the generic
            // message in Production to avoid leaking configuration detail.
            var message = result.ErrorKind == AgentTurnErrorKind.Configuration
                && _environment.IsDevelopment()
                && !string.IsNullOrWhiteSpace(result.Error)
                    ? result.Error!
                    : "The agent was unable to process your request.";

            await writer.WriteAsync(new RunErrorEvent(message), ct);
            return;
        }

        var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, record.AgentName);
        if (result.ToolsInvoked.Count > 0)
            OrchestrationMetrics.ToolCalls.Add(result.ToolsInvoked.Count, agentTag);

        UserActivityMetrics.Turns.Add(1,
            new KeyValuePair<string, object?>(UserConventions.UserId, callerId),
            new KeyValuePair<string, object?>(AgentConventions.Name, record.AgentName));

        // Fold this turn's tokens into the conversation-lifetime budget so a subsequent run is
        // declined once the cumulative ceiling is crossed. No-op when the budget is disabled.
        await _conversationBudget.RecordUsageAsync(
            input.ThreadId, result.InputTokens + result.OutputTokens, ct);

        // One call for what used to be an accumulate, a twelve-argument store write, a second store
        // write and a swallow — spelled out identically in three files, and drifted in four ways
        // between them (issue #280).
        await _telemetryRecorder.RecordTurnAsync(
            telemetry,
            new ConversationTurnTelemetry(
                result.InputTokens, result.OutputTokens, result.CacheRead, result.CacheWrite,
                result.CostUsd, result.ToolsInvoked.Count, result.Model),
            ct);

        // Stream and persist the assistant response under a single stable id. The client
        // references this id (via TEXT_MESSAGE_START) for retry-from-message, so the streamed
        // id and the persisted id MUST match. All TEXT_MESSAGE_* events for this message share it.
        var assistantId = Guid.NewGuid();
        var messageId = assistantId.ToString();
        await writer.WriteAsync(new TextMessageStartEvent(messageId, "assistant"), ct);

        var response = result.Response;
        for (var i = 0; i < response.Length; i += ChunkSize)
        {
            var chunk = response.Substring(i, Math.Min(ChunkSize, response.Length - i));
            await writer.WriteAsync(new TextMessageContentEvent(messageId, chunk), ct);
        }

        await writer.WriteAsync(new TextMessageEndEvent(messageId), ct);

        // Persist the assistant response under the same id that was streamed to the client.
        var assistantMsg = new ConversationMessage(
            assistantId,
            MessageRole.Assistant,
            response,
            DateTimeOffset.UtcNow,
            ToolCalls: result.ToolCalls);
        await _conversationStore.AppendMessageAsync(input.ThreadId, callerId, assistantMsg, ct);

        await writer.WriteAsync(new RunFinishedEvent(input.ThreadId, input.RunId), ct);
    }

    /// <summary>
    /// Emits the graceful "budget exhausted" turn over the AG-UI protocol: a normal assistant text
    /// message (not a RunErrorEvent) carrying the explanatory text, persisted under a stable id, then
    /// <c>RunFinished</c>. No LLM is dispatched.
    /// </summary>
    private async Task EmitBudgetExhaustedAsync(
        IAgUiEventWriter writer, RunAgentInput input, string agentName, string callerId, CancellationToken ct)
    {
        var message = Services.ConversationBudgetNotice.Message;

        _logger.LogWarning(
            "AG-UI run {RunId}: declined — conversation {ThreadId} lifetime token budget exhausted",
            input.RunId, input.ThreadId);
        OrchestrationMetrics.ConversationsBudgetStopped.Add(
            1, new KeyValuePair<string, object?>(AgentConventions.Name, agentName));

        var assistantId = Guid.NewGuid();
        var messageId = assistantId.ToString();
        await writer.WriteAsync(new TextMessageStartEvent(messageId, "assistant"), ct);
        await writer.WriteAsync(new TextMessageContentEvent(messageId, message), ct);
        await writer.WriteAsync(new TextMessageEndEvent(messageId), ct);

        var assistantMsg = new ConversationMessage(
            assistantId, MessageRole.Assistant, message, DateTimeOffset.UtcNow);
        await _conversationStore.AppendMessageAsync(input.ThreadId, callerId, assistantMsg, ct);

        await writer.WriteAsync(new RunFinishedEvent(input.ThreadId, input.RunId), ct);
    }

    /// <summary>
    /// Resolves the caller's stable identity through the single authority.
    /// </summary>
    /// <remarks>
    /// This used to hand-roll the lookup with a comment saying it "mirrors" the shared extension. It
    /// stopped mirroring it the moment the shared ladder learned to accept <c>sub</c>: a sub-only token
    /// resolved everywhere else in the harness but threw here. That is the drift
    /// <c>CallerIdentityResolutionBoundaryTests</c> now prevents — delegate, never copy.
    /// </remarks>
    private static string GetCallerId(ClaimsPrincipal principal) => principal.GetUserId();

    /// <summary>
    /// Parses a client-supplied message id into a <see cref="Guid"/>, falling back to a freshly
    /// generated id when the client omits it or sends a value that is not a valid GUID. Preserving
    /// the client id keeps the optimistic UI message and the persisted record in sync so that
    /// retry/edit operations (keyed by message id) resolve to a stored message.
    /// </summary>
    private static Guid ParseClientId(string? clientId) =>
        Guid.TryParse(clientId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : Guid.NewGuid();

    // Delegates to the shared projection rather than repeating the role switch. This file, the SignalR
    // orchestrator and the durable multi-turn loop each carried a byte-identical copy; a role added to
    // one of three copies does not fail, it silently replays as the fallback. Not static: gates
    // tool-call expansion on the live IToolCallReplayTreatment.Enabled value so an operator's kill
    // switch stops replaying already-persisted tool payloads, not just stop writing new ones.
    private IReadOnlyList<ChatMessage> ToMeaiHistory(IReadOnlyList<ConversationMessage> messages) =>
        ConversationMessageMapping.ToChatMessages(
            messages,
            _toolCallReplayTreatment.Enabled,
            _toolCallReplayTreatment.MaxReplayedChars,
            _logger);

    private static async Task TryWriteErrorAsync(IAgUiEventWriter writer, string message, CancellationToken ct)
    {
        try
        {
            await writer.WriteAsync(new RunErrorEvent(message), ct);
        }
        catch
        {
            // Stream may already be closed — swallow silently.
        }
    }
}
