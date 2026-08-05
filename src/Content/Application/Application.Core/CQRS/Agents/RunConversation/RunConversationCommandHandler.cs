using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Governance;
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
	private readonly IConversationStore _conversationStore;
	private readonly IConversationTurnLease _turnLease;
	private readonly IOptions<ConversationsConfig> _conversationsConfig;
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
		IConversationStore conversationStore,
		IConversationTurnLease turnLease,
		IOptions<ConversationsConfig> conversationsConfig,
		ILogger<RunConversationCommandHandler> logger)
	{
		ArgumentNullException.ThrowIfNull(mediator);
		ArgumentNullException.ThrowIfNull(agentCache);
		ArgumentNullException.ThrowIfNull(conversationBudget);
		ArgumentNullException.ThrowIfNull(observabilityStore);
		ArgumentNullException.ThrowIfNull(conversationStore);
		ArgumentNullException.ThrowIfNull(turnLease);
		ArgumentNullException.ThrowIfNull(conversationsConfig);
		ArgumentNullException.ThrowIfNull(logger);

		_mediator = mediator;
		_agentCache = agentCache;
		_conversationBudget = conversationBudget;
		_observabilityStore = observabilityStore;
		_conversationStore = conversationStore;
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

		var transcript = new DurableTranscript(_conversationStore, request.ConversationId, ownerId);

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
		var totalToolInvocations = 0;
		var governanceTraces = new List<GovernanceTrace>();
		AgentTurnResult? lastResult = null;
		var stoppedForBudget = false;

		// Running token/cost aggregates for session-level metrics
		int totalInputTokens = 0, totalOutputTokens = 0, totalCacheRead = 0, totalCacheWrite = 0;
		decimal totalCostUsd = 0m;
		string? sessionModel = null;

		var dbSessionId = await _observabilityStore.StartSessionAsync(
			request.ConversationId, request.AgentName, null, cancellationToken);

		var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, request.AgentName);
		var sessionTags = new TagList { { AgentConventions.Name, request.AgentName } };
		SessionMetrics.SessionsStarted.Add(1, agentTag);
		SessionMetrics.ActiveSessions.Add(1, sessionTags);

		// Tracks whether the observability session has already been ended on a
		// normal (success / turn-failure) return path, so the catch block does
		// not double-end it when an exception escapes after those paths.
		var sessionEnded = false;

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
					TurnNumber = index + 1,
					ObservabilitySessionId = dbSessionId
				};

				lastResult = await _mediator.Send(turnCommand, cancellationToken);

				if (!lastResult.Success)
				{
					// A cancelled turn (e.g. caller disconnect) is routine, not a failure:
					// route it into the OperationCanceledException handler below so the session
					// ends "cancelled" rather than "error", consistent with the other transports.
					if (lastResult.ErrorKind == AgentTurnErrorKind.Cancelled)
						throw new OperationCanceledException(cancellationToken);

					_logger.LogError("Conversation turn {Turn} failed for {AgentName}: {Error}",
						index + 1, request.AgentName, lastResult.Error);

					await _observabilityStore.EndSessionAsync(
						dbSessionId, "error", lastResult.Error, cancellationToken);
					sessionEnded = true;

					return new ConversationResult
					{
						Success = false,
						Turns = turns,
						FinalResponse = string.Empty,
						TotalToolInvocations = totalToolInvocations,
						TotalTokens = totalInputTokens + totalOutputTokens,
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
				// A turn that succeeds with NO text is not a complete exchange either, and storing it
				// would produce the very half-turn described above by a longer route: both stores drop
				// empty-content messages from the dispatch window (that is how widget messages are kept
				// out of prompts), so the answer would be written, filtered out on the next read, and
				// leave the question standing alone. A turn can end this way legitimately — a model
				// replying with tool calls and no prose — so this is skipped rather than treated as a
				// failure, and logged so it is visible rather than silent.
				if (transcript is not null)
				{
					if (string.IsNullOrWhiteSpace(lastResult.Response))
					{
						_logger.LogWarning(
							"Turn {Turn} of conversation {ConversationId} produced no text; not persisted, "
							+ "because an empty answer is filtered from the replay window and would leave "
							+ "the question unanswered.",
							index + 1, request.ConversationId);
					}
					else
					{
						await transcript.AppendTurnAsync(userMessage, lastResult.Response, cancellationToken);
					}
				}

				turns.Add(new TurnSummary
				{
					TurnNumber = index + 1,
					UserMessage = userMessage,
					AgentResponse = lastResult.Response,
					ToolsInvoked = lastResult.ToolsInvoked
				});

				totalToolInvocations += lastResult.ToolsInvoked.Count;

				if (lastResult.Governance is not null)
					governanceTraces.Add(lastResult.Governance);

				totalInputTokens += lastResult.InputTokens;
				totalOutputTokens += lastResult.OutputTokens;
				totalCacheRead += lastResult.CacheRead;
				totalCacheWrite += lastResult.CacheWrite;
				totalCostUsd += lastResult.CostUsd;
				sessionModel ??= lastResult.Model;

				// Fold this turn's input+output into the conversation-lifetime budget (mirrors the
				// per-turn TokenBudgetBehavior's accounting). The next loop iteration's gate decides
				// whether the cumulative total has crossed the ceiling.
				await _conversationBudget.RecordUsageAsync(
					request.ConversationId,
					lastResult.InputTokens + lastResult.OutputTokens,
					cancellationToken);

				var totalInput = totalInputTokens + totalCacheRead;
				var cacheHitRate = totalInput > 0 ? (decimal)totalCacheRead / totalInput : 0m;

				await _observabilityStore.UpdateSessionMetricsAsync(
					dbSessionId, index + 1, totalToolInvocations, 0,
					totalInputTokens, totalOutputTokens, totalCacheRead, totalCacheWrite,
					totalCostUsd, Math.Round(cacheHitRate, 4), sessionModel, cancellationToken);

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
			_logger.LogInformation("Conversation completed: {TurnCount} turns, {ToolCount} tool invocations",
				turns.Count, totalToolInvocations);

			OrchestrationMetrics.ConversationDuration.Record(sw.Elapsed.TotalMilliseconds, agentTag);
			OrchestrationMetrics.TurnsPerConversation.Record(turns.Count, agentTag);
			if (totalToolInvocations > 0)
				OrchestrationMetrics.ToolCalls.Add(totalToolInvocations, agentTag);

			if (totalCostUsd > 0)
				SessionMetrics.SessionCost.Record((double)totalCostUsd, agentTag);

			await _observabilityStore.EndSessionAsync(
				dbSessionId, "completed", null, cancellationToken);
			sessionEnded = true;

			return new ConversationResult
			{
				Success = true,
				Turns = turns,
				FinalResponse = lastResult?.Response ?? string.Empty,
				TotalToolInvocations = totalToolInvocations,
				TotalTokens = totalInputTokens + totalOutputTokens,
				BudgetExhausted = stoppedForBudget,
				Governance = governanceTraces.Count > 0 ? GovernanceTrace.Merge(governanceTraces) : null
			};
		}
		catch (OperationCanceledException)
		{
			// Caller cancellation (e.g. client disconnect) is routine, not exceptional.
			// End the session as cancelled using a non-cancelled token so the cleanup
			// write still completes, then rethrow to preserve cancellation semantics.
			await EndSessionSafelyAsync(dbSessionId, "cancelled", null, sessionEnded);
			sessionEnded = true;
			throw;
		}
		catch (Exception ex)
		{
			// Log the full exception via structured logging; never persist the raw
			// message to the session row (it can leak internal detail). End the
			// session with a stable scrubbed status code and rethrow.
			_logger.LogError(ex, "Conversation with {AgentName} failed with an unhandled exception",
				request.AgentName);
			await EndSessionSafelyAsync(dbSessionId, "error", "conversation.unhandled_exception", sessionEnded);
			sessionEnded = true;
			throw;
		}
		finally
		{
			// Decrement the up-down gauge exactly once on every exit path so the
			// ActiveSessions metric cannot skew permanently when the try block throws.
			SessionMetrics.ActiveSessions.Add(-1, sessionTags);
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

	/// <summary>
	/// Ends the observability session defensively during exception/cancellation
	/// handling: skips the write if the session was already ended on a normal
	/// return path, uses a non-cancelled token so cleanup completes even when the
	/// caller's token is cancelled, and never lets a cleanup failure mask the
	/// original exception being propagated.
	/// </summary>
	private async Task EndSessionSafelyAsync(Guid sessionId, string status, string? reason, bool alreadyEnded)
	{
		if (alreadyEnded)
			return;

		try
		{
			await _observabilityStore.EndSessionAsync(sessionId, status, reason, CancellationToken.None);
		}
		catch (Exception endEx)
		{
			_logger.LogError(endEx, "Failed to end observability session {SessionId} during cleanup", sessionId);
		}
	}
}
