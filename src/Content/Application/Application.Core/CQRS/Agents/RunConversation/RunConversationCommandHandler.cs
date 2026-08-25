using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Models.Conversations;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Governance;
using Domain.AI.Observability.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI.Conversations;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Agents.RunConversation;

/// <summary>
/// Handles <see cref="RunConversationCommand"/> by executing sequential turns
/// with the specified agent, feeding each response back as context.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two modes, one loop.</strong> Without <see cref="RunConversationCommand.ConversationOwnerId"/>
/// the run is self-contained: it starts from nothing and leaves nothing behind. With it, the run
/// <em>continues</em> a durable conversation — prior turns seed the first dispatch, every turn is
/// persisted as it completes, and the run holds the conversation's turn lease throughout.
/// </para>
/// <para>
/// The continuation logic lives here, in the one place turns are actually executed, rather than in the
/// Execution API caller that first needed it (issue #235). Putting it in the caller would have made
/// durability visible only as a finished lump — a run that died on its seventh turn would persist
/// nothing — and would have left the next consumer to build it again.
/// </para>
/// </remarks>
public class RunConversationCommandHandler : IRequestHandler<RunConversationCommand, ConversationResult>
{
	private readonly IMediator _mediator;
	private readonly IAgentConversationCache _agentCache;
	private readonly IConversationBudgetTracker _conversationBudget;
	private readonly IObservabilityStore _observabilityStore;
	private readonly IConversationTelemetryRecorder _telemetryRecorder;
	private readonly IConversationStore _conversationStore;
	private readonly IConversationTurnLease _turnLease;
	private readonly IOptions<ConversationsConfig> _conversationsConfig;
	private readonly IToolCallReplayTreatment _toolCallReplayTreatment;
	private readonly ILogger<RunConversationCommandHandler> _logger;

	/// <summary>Initializes a new <see cref="RunConversationCommandHandler"/>.</summary>
	/// <param name="mediator">Dispatches each turn.</param>
	/// <param name="agentCache">Per-conversation agent cache, evicted when the run ends.</param>
	/// <param name="conversationBudget">The conversation-lifetime token ceiling, gated before every turn.</param>
	/// <param name="observabilityStore">Session-level telemetry for the run.</param>
	/// <param name="conversationStore">
	/// The durable transcript. Used only when the command carries an owner; it also enforces ownership,
	/// which is why this handler never compares owners itself.
	/// </param>
	/// <param name="turnLease">
	/// Serialises turns on one conversation across hosts. Held for a whole durable run.
	/// </param>
	/// <param name="conversationsConfig">Supplies the bounded replay window.</param>
	/// <param name="logger">Diagnostic logger.</param>
	/// <remarks>
	/// The last three are ordinary required dependencies rather than optional ones, even though a
	/// self-contained run never touches them. A host that composes this handler without conversation
	/// storage then fails at startup, which is a fixable misconfiguration, instead of on its first
	/// durable run, which is an outage.
	/// </remarks>
	public RunConversationCommandHandler(
		IMediator mediator,
		IAgentConversationCache agentCache,
		IConversationBudgetTracker conversationBudget,
		IObservabilityStore observabilityStore,
		IConversationTelemetryRecorder telemetryRecorder,
		IConversationStore conversationStore,
		IConversationTurnLease turnLease,
		IOptions<ConversationsConfig> conversationsConfig,
		IToolCallReplayTreatment toolCallReplayTreatment,
		ILogger<RunConversationCommandHandler> logger)
	{
		ArgumentNullException.ThrowIfNull(mediator);
		ArgumentNullException.ThrowIfNull(agentCache);
		ArgumentNullException.ThrowIfNull(conversationBudget);
		ArgumentNullException.ThrowIfNull(observabilityStore);
		ArgumentNullException.ThrowIfNull(telemetryRecorder);
		ArgumentNullException.ThrowIfNull(conversationStore);
		ArgumentNullException.ThrowIfNull(turnLease);
		ArgumentNullException.ThrowIfNull(conversationsConfig);
		ArgumentNullException.ThrowIfNull(toolCallReplayTreatment);
		ArgumentNullException.ThrowIfNull(logger);

		_mediator = mediator;
		_agentCache = agentCache;
		_conversationBudget = conversationBudget;
		_observabilityStore = observabilityStore;
		_telemetryRecorder = telemetryRecorder;
		_conversationStore = conversationStore;
		_toolCallReplayTreatment = toolCallReplayTreatment;
		_turnLease = turnLease;
		_conversationsConfig = conversationsConfig;
		_logger = logger;
	}

	/// <inheritdoc/>
	public Task<ConversationResult> Handle(RunConversationCommand request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		// Only an ABSENT owner opts out. A blank one falls through to the durable path, where the store
		// rejects it — the same fail-closed reading the store documents, and the reason this is not an
		// IsNullOrWhiteSpace test: an empty identity has been read as "everyone" in this codebase before,
		// and treating it as "nobody in particular, carry on" is how that happens again.
		return request.ConversationOwnerId is null
			? RunAsync(request, transcript: null, cancellationToken)
			: RunDurableAsync(request, cancellationToken);
	}

	/// <summary>
	/// Runs the conversation against its durable transcript: opens it, takes its turn lease, replays the
	/// bounded history window, and hands the loop a transcript to write each turn back to.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <strong>Order is load-bearing.</strong> The conversation is opened <em>before</em> the lease is
	/// taken because a durable lease claims an existing conversation row and throws when there is none —
	/// so the lease cannot be what protects the opening. That is why opening is a single atomic
	/// <see cref="IConversationStore.GetOrCreateAsync"/> rather than a read followed by a create: two
	/// runs opening the same new conversation both see it absent, and the composed version lets the
	/// loser's create delete the winner's turns.
	/// </para>
	/// <para>
	/// <strong>Losing the lease mid-run cancels the run.</strong> Another host holding the lease means
	/// any turn written from here on is exactly the concurrent turn the lease exists to prevent, so the
	/// lost-lease token is linked into the token every turn runs under.
	/// </para>
	/// </remarks>
	private async Task<ConversationResult> RunDurableAsync(
		RunConversationCommand request, CancellationToken cancellationToken)
	{
		var ownerId = request.ConversationOwnerId!;

		await _conversationStore.GetOrCreateAsync(
			request.AgentName, ownerId, request.ConversationId, cancellationToken);

		await using var lease = await _turnLease.AcquireAsync(request.ConversationId, cancellationToken);
		using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken, lease.LeaseLost);

		var transcript = new DurableTranscript(
			_conversationStore,
			request.ConversationId,
			ownerId,
			_toolCallReplayTreatment.Enabled,
			_toolCallReplayTreatment.MaxReplayedChars,
			_logger);

		return await RunAsync(request, transcript, turnCts.Token);
	}

	private async Task<ConversationResult> RunAsync(
		RunConversationCommand request,
		DurableTranscript? transcript,
		CancellationToken cancellationToken)
	{
		// Derived from the transcript rather than passed alongside it: the two are one piece of state,
		// and a signature that took both would let a caller pass a seed with no transcript, or a window
		// belonging to some other conversation, with nothing to object.
		//
		// Read here rather than before the lease was taken: the turn this run queued behind may have
		// appended to the transcript, and a window read earlier would omit exactly the messages that
		// turn just wrote.
		var seedHistory = transcript is null
			? []
			: await transcript.LoadHistoryAsync(
				_conversationsConfig.Value.MaxHistoryMessages, cancellationToken);

		if (transcript is not null)
		{
			_logger.LogInformation(
				"Continuing durable conversation {ConversationId} with {HistoryCount} prior message(s) replayed.",
				request.ConversationId, seedHistory.Count);
		}

		_logger.LogInformation("Starting conversation with {AgentName}, {MessageCount} messages, max {MaxTurns} turns",
			request.AgentName, request.UserMessages.Count, request.MaxTurns);

		var sw = Stopwatch.StartNew();
		var turns = new List<TurnSummary>();
		var governanceTraces = new List<GovernanceTrace>();
		AgentTurnResult? lastResult = null;
		var stoppedForBudget = false;
		string? sessionModel = null;

		// Everything the conversation has spent, this run included as it goes, plus the session it is
		// recorded against — adopted when the conversation already has one, opened when it does not.
		// A durable run resumes from the store; a self-contained run has nothing before it, starts at
		// zero, and writes nothing back because there is no record to write to.
		//
		// This is the one implementation all three transports share (issue #280). The rule it enforces
		// is that a conversation gets ONE session for its whole life: re-opening the session it already
		// has does not open a second, it restamps the first one's start time, and every duration derived
		// from it then describes only the latest run (issue #255).
		var telemetry = await _telemetryRecorder.BeginAsync(
			request.ConversationId, request.ConversationOwnerId, request.AgentName, null, cancellationToken);

		var dbSessionId = telemetry.SessionId;
		var conversationTotals = telemetry.Totals;

		// Where this run came in. The caller asked what THIS call cost, and the run-scoped metrics
		// report the same, so that quantity is derived by subtraction rather than accumulated alongside:
		// two counters over one stream of turns are two chances to disagree, and a pair of totals
		// disagreeing about the same turns is the whole of issue #255.
		var runBaseline = conversationTotals;

		var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, request.AgentName);

		// A run in flight, not a session: the session belongs to the conversation and outlives this run
		// whenever the conversation is durable. Paired with the decrement in the finally below.
		OrchestrationMetrics.RunsActive.Add(1, agentTag);

		// A run is not the conversation. Only a self-contained run may end the session, because only
		// there are the two the same thing; ending it on a durable run would mark a conversation
		// finished that the next run — or a user still typing in the interactive host — is about to
		// continue. The interactive transports have never ended a session per turn for this reason.
		var ownsSession = transcript is null;

		// Tracks whether the observability session has already been ended on a
		// normal (success / turn-failure) return path, so the catch block does
		// not double-end it when an exception escapes after those paths.
		var sessionEnded = false;

		// Every path that finishes the session goes through here, so the two rules — this run must own
		// the session, and it must not end one twice — are decided once. They were applied at each of
		// the four call sites instead, and review found the exception path had been missed, which ended
		// a live conversation's session on any transient throw.
		//
		// Failures are logged and swallowed rather than propagated. Ending a session is bookkeeping;
		// a bookkeeping failure must not turn a conversation that completed into one that reports an
		// error, and it must never mask an exception already on its way out. Cleanup runs on an
		// uncancelled token so it still completes when the caller's token is the reason we are here.
		async Task EndRunSessionAsync(SessionStatus status, string? reason)
		{
			if (sessionEnded || !ownsSession)
				return;

			sessionEnded = true;

			try
			{
				await _observabilityStore.EndSessionAsync(dbSessionId, status, reason, CancellationToken.None);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to end observability session {SessionId}", dbSessionId);
			}
		}

		try
		{
			foreach (var (userMessage, index) in request.UserMessages.Select((m, i) => (m, i)))
			{
				if (index >= request.MaxTurns)
				{
					_logger.LogWarning("Max turns ({MaxTurns}) reached for {AgentName}", request.MaxTurns, request.AgentName);
					break;
				}

				// Conversation-lifetime budget gate: checked before starting a turn so a conversation
				// that exhausted its cumulative token ceiling on a prior turn stops gracefully here
				// rather than running another.
				//
				// This turn is NOT exempt just because it is the run's first. That used to be true, and
				// stopped being true when a conversation started outliving the run carrying it: the
				// budget is keyed by conversation and is durable, so a run continuing a conversation that
				// was already exhausted declines before its first dispatch — which is the whole point of
				// a lifetime ceiling. A self-contained run still proceeds, because nothing has been
				// recorded under an id nobody has used before.
				var budgetStatus = await _conversationBudget.GetStatusAsync(
					request.ConversationId, cancellationToken);

				if (budgetStatus.IsExhausted)
				{
					stoppedForBudget = true;
					_logger.LogWarning(
						"Conversation {ConversationId} stopped: lifetime token budget exhausted after {Turns} turn(s)",
						request.ConversationId, turns.Count);
					OrchestrationMetrics.ConversationsBudgetStopped.Add(1, agentTag);
					break;
				}

				if (request.OnProgress != null)
				{
					await request.OnProgress(new TurnProgress
					{
						TurnNumber = index + 1,
						AgentName = request.AgentName,
						Status = "executing"
					});
				}

				var turnCommand = new ExecuteAgentTurnCommand
				{
					AgentName = request.AgentName,
					UserMessage = userMessage,

					// The seed is used only by the first turn; from then on each turn carries the one
					// before it, and UpdatedHistory already includes whatever was passed in — so the
					// replayed transcript flows through the rest of the run without being re-read.
					ConversationHistory = lastResult?.UpdatedHistory ?? seedHistory,
					ConversationId = request.ConversationId,

					// Numbered across the conversation, not within the run. Per-turn observability rows
					// are keyed by conversation and turn number, so a run that restarted at 1 would
					// overwrite the first turns of the run before it (issue #255).
					TurnNumber = conversationTotals.TurnCount + 1,
					ObservabilitySessionId = dbSessionId
				};

				lastResult = await _mediator.Send(turnCommand, cancellationToken);

				if (!lastResult.Success)
				{
					// A cancelled turn (e.g. caller disconnect) is routine, not a failure: route it into
					// the OperationCanceledException handler below, which rethrows rather than returning
					// a failed result and records the cancellation in the session's reason.
					if (lastResult.ErrorKind == AgentTurnErrorKind.Cancelled)
						throw new OperationCanceledException(cancellationToken);

					_logger.LogError("Conversation turn {Turn} failed for {AgentName}: {Error}",
						index + 1, request.AgentName, lastResult.Error);

					await EndRunSessionAsync(SessionStatus.Error, lastResult.Error);

					var partialShare = conversationTotals.Since(runBaseline);

					return new ConversationResult
					{
						Success = false,
						Turns = turns,
						FinalResponse = string.Empty,
						TotalToolInvocations = partialShare.ToolCallCount,
						TotalTokens = partialShare.InputTokens + partialShare.OutputTokens,
						Error = $"Turn {index + 1} failed: {lastResult.Error}"
					};
				}

				// Write the turn as soon as it succeeds, rather than writing the whole run back at the
				// end: a run that dies on its seventh turn keeps the six that completed, which is the
				// difference between a durable conversation and a durable summary of one.
				//
				// Question and answer are written TOGETHER, and only once there is an answer. Writing
				// the question up-front — which is what the interactive transports do, so that a live
				// user can see what they asked — is wrong here for two reasons that both come back to
				// this transcript being REPLAYED to a model rather than read by a person. A turn that
				// fails, or one cut short by a lost lease, would leave a question with no answer, and
				// the next run would replay a conversation in which the user apparently asked twice
				// and was ignored once. And the second write happens on a token the lost lease has
				// already cancelled, so the pair would be split precisely when the lease was taken —
				// the one moment the transcript must not be half-written.
				//
				// A turn that succeeds with NO text and NO tool calls is not a complete exchange, and
				// storing it would produce the very half-turn described above by a longer route: both
				// stores drop a message with neither (that is how widget messages are kept out of
				// prompts), so the answer would be written, filtered out on the next read, and leave the
				// question standing alone. A turn that ends in tool calls with no prose IS a complete,
				// storable exchange — both stores now keep a row with tool calls even when Content is
				// empty (#249 item 6) specifically so this case persists rather than silently losing the
				// tool activity along with the missing text.
				if (transcript is not null)
				{
					if (string.IsNullOrWhiteSpace(lastResult.Response) && lastResult.ToolCalls.Count == 0)
					{
						_logger.LogWarning(
							"Turn {Turn} of conversation {ConversationId} produced no text and no tool calls; "
							+ "not persisted, because an empty answer is filtered from the replay window and "
							+ "would leave the question unanswered.",
							index + 1, request.ConversationId);
					}
					else
					{
						await transcript.AppendTurnAsync(
							userMessage, lastResult.Response, lastResult.ToolCalls, cancellationToken);
					}
				}

				turns.Add(new TurnSummary
				{
					TurnNumber = index + 1,
					UserMessage = userMessage,
					AgentResponse = lastResult.Response,
					ToolsInvoked = lastResult.ToolsInvoked
				});

				if (lastResult.Governance is not null)
					governanceTraces.Add(lastResult.Governance);

				sessionModel ??= lastResult.Model;

				// Fold this turn's input+output into the conversation-lifetime budget (mirrors the
				// per-turn TokenBudgetBehavior's accounting). The next loop iteration's gate decides
				// whether the cumulative total has crossed the ceiling.
				await _conversationBudget.RecordUsageAsync(
					request.ConversationId,
					lastResult.InputTokens + lastResult.OutputTokens,
					cancellationToken);

				// One call for the accumulate and both writes — the observability rollup and the
				// conversation's own copy of the same totals, always from one value so the two cannot
				// disagree. Cumulative, not this run's share: the session row is keyed one-per-conversation
				// and written with SET semantics, so a run's own totals would replace the conversation's
				// with whatever the latest run happened to spend (issues #255, #280). It never throws —
				// the turn's answer is already in the transcript, and discarding real work to protect a
				// number is the wrong trade.
				telemetry = await _telemetryRecorder.RecordTurnAsync(
					telemetry,
					new ConversationTurnTelemetry(
						lastResult.InputTokens, lastResult.OutputTokens,
						lastResult.CacheRead, lastResult.CacheWrite,
						lastResult.CostUsd, lastResult.ToolsInvoked.Count, sessionModel),
					cancellationToken);

				conversationTotals = telemetry.Totals;

				if (request.OnProgress != null)
				{
					await request.OnProgress(new TurnProgress
					{
						TurnNumber = index + 1,
						AgentName = request.AgentName,
						Status = "completed",
						Response = lastResult.Response
					});
				}
			}

			sw.Stop();

			// What this run alone spent, taken as the difference between where the conversation stands
			// now and where it stood when the run started. Everything below reports on the run, not the
			// conversation, so all of it reads from here.
			var runShare = conversationTotals.Since(runBaseline);

			_logger.LogInformation("Conversation completed: {TurnCount} turns, {ToolCount} tool invocations",
				turns.Count, runShare.ToolCallCount);

			OrchestrationMetrics.ConversationDuration.Record(sw.Elapsed.TotalMilliseconds, agentTag);
			OrchestrationMetrics.TurnsPerConversation.Record(turns.Count, agentTag);
			if (runShare.ToolCallCount > 0)
				OrchestrationMetrics.ToolCalls.Add(runShare.ToolCallCount, agentTag);

			if (runShare.CostUsd > 0)
				SessionMetrics.SessionCost.Record((double)runShare.CostUsd, agentTag);

			await EndRunSessionAsync(SessionStatus.Completed, null);

			return new ConversationResult
			{
				Success = true,
				Turns = turns,
				FinalResponse = lastResult?.Response ?? string.Empty,
				TotalToolInvocations = runShare.ToolCallCount,
				TotalTokens = runShare.InputTokens + runShare.OutputTokens,
				BudgetExhausted = stoppedForBudget,
				Governance = governanceTraces.Count > 0 ? GovernanceTrace.Merge(governanceTraces) : null
			};
		}
		// The filter is load-bearing, not decoration. TaskCanceledException derives from
		// OperationCanceledException and is what an HTTP client throws when a request exceeds its
		// timeout — with nobody having cancelled anything. Catching the base type unfiltered would
		// file a model call that timed out as a deliberate stop: absent from the error rate, and
		// silent, because only the general handler below logs. Asking the token whether cancellation
		// was actually requested is what separates "the caller walked away" from "the call failed".
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Caller cancellation (e.g. client disconnect) is routine, not exceptional, and the exception
			// is rethrown rather than turned into a failed result: a caller that walked away is not a run
			// that failed.
			//
			// It closes as Cancelled, a state of its own again as of #301. This path is where the
			// string "cancelled" came from: the schema rejected the word, the store logged and
			// swallowed the rejected write, and the session was never ended at all. It then spent a
			// release closing as Error — counted among the failures on every dashboard — because
			// nothing could deliver a widened constraint to a database that already held data.
			await EndRunSessionAsync(SessionStatus.Cancelled, "conversation.cancelled");
			throw;
		}
		catch (Exception ex)
		{
			// Log the full exception via structured logging; never persist the raw
			// message to the session row (it can leak internal detail). End the
			// session with a stable scrubbed status code and rethrow.
			_logger.LogError(ex, "Conversation with {AgentName} failed with an unhandled exception",
				request.AgentName);
			await EndRunSessionAsync(SessionStatus.Error, "conversation.unhandled_exception");
			throw;
		}
		finally
		{
			// Decrement the up-down gauge exactly once on every exit path so the runs-in-flight
			// metric cannot skew permanently when the try block throws.
			OrchestrationMetrics.RunsActive.Add(-1, agentTag);
			_agentCache.Evict(request.ConversationId);

			// The conversation budget is deliberately NOT released here. This handler used to, on the
			// premise that it owned the conversation's whole lifecycle — but a conversation now
			// continues across runs and across hosts (issue #235), so the end of this run is not the
			// end of the conversation. Releasing would reset the accumulated total, handing every
			// subsequent run a fresh ceiling and making a lifetime budget a per-run one. The two
			// interactive callers have never released for the same reason; reclamation is retention's
			// job, not a turn's.
		}
	}

}
