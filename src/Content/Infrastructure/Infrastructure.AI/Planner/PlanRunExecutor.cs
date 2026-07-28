using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Services.Governance;
using Domain.AI.Planner;
using Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Planner;

/// <summary>
/// The single arming site for enveloped plan runs: initializes the scoped governance identity, publishes
/// the caller's capability envelope ambiently, and drives <see cref="IPlanExecutor"/> inside that scope.
/// Modeled directly on <c>BundleRunExecutor</c> — see <see cref="IPlanRunExecutor"/> for the doctrine.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ordering that preserves the invariants.</strong> A fresh DI scope is created per run so the
/// scoped <see cref="IAgentExecutionContext"/>, tool-invocation governor, and step executors all share one
/// identity that cannot leak across runs. The execution context is initialized <em>before</em> the envelope
/// is armed: the governor fails closed on any identity-less tool call made under an ambient envelope (by
/// design), so arming the envelope first would open a window where a concurrently scheduled step is denied
/// for the wrong reason. The envelope scope is disposed in a <c>finally</c> (via <c>using</c>) on every
/// path, and the plan summary is fully materialised before disposal — no deferred enumeration outlives the
/// scope.
/// </para>
/// <para>
/// <strong>Fail-closed posture.</strong> This path cannot run un-enveloped: a missing envelope or blank
/// agent identity is rejected with a stable error code before any plan state is touched. Unexpected
/// exceptions are logged in full but surfaced only as <c>plan_run.execution_failed</c> — raw exception
/// text never reaches the caller. That guard spans identity initialization and executor resolution as
/// well as the run itself, so <see cref="IPlanRunExecutor"/>'s unqualified "never raw exception text"
/// holds literally rather than by happening to have no throwing setup.
/// </para>
/// </remarks>
public sealed class PlanRunExecutor : IPlanRunExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConversationBudgetTracker _conversationBudget;
    private readonly ILogger<PlanRunExecutor> _logger;

    /// <summary>Initializes a new <see cref="PlanRunExecutor"/>.</summary>
    /// <param name="scopeFactory">Creates the per-run DI scope the identity and executor resolve from.</param>
    /// <param name="conversationBudget">Holds the run-level token budget this executor owns and releases.</param>
    /// <param name="logger">Structured logger for run auditing.</param>
    public PlanRunExecutor(
        IServiceScopeFactory scopeFactory,
        IConversationBudgetTracker conversationBudget,
        ILogger<PlanRunExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(conversationBudget);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _conversationBudget = conversationBudget;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Envelope is null)
        {
            _logger.LogWarning("Plan run {PlanId} rejected: no capability envelope supplied", request.PlanId);
            return Result<PlanExecutionSummary>.Fail("plan_run.envelope_required");
        }

        if (string.IsNullOrWhiteSpace(request.AgentId))
        {
            _logger.LogWarning("Plan run {PlanId} rejected: no agent identity supplied", request.PlanId);
            return Result<PlanExecutionSummary>.Fail("plan_run.agent_identity_required");
        }

        // Null means "derive the run scope from the plan id". A supplied value must be well formed:
        // blank would yield a blank run scope, which downstream IsNullOrEmpty checks read as "no run
        // scope", silently disabling the run-level budget gate.
        if (request.ConversationId is not null
            && !PlanRunRequest.IsWellFormedAgentId(request.ConversationId))
        {
            _logger.LogWarning(
                "Plan run {PlanId} rejected: conversation id is malformed (length {Length})",
                request.PlanId, request.ConversationId.Length);
            return Result<PlanExecutionSummary>.Fail("plan_run.conversation_id_invalid");
        }

        if (!PlanRunRequest.IsWellFormedAgentId(request.AgentId))
        {
            // The id is the permission-resolution key and the audit subject. The rejected value is
            // deliberately not echoed back or logged verbatim — it is attacker-controlled text.
            _logger.LogWarning(
                "Plan run {PlanId} rejected: agent identity is malformed (length {Length})",
                request.PlanId, request.AgentId.Length);
            return Result<PlanExecutionSummary>.Fail("plan_run.agent_identity_invalid");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();

        var runScope = request.ConversationId ?? request.PlanId.Value.ToString();

        // Scope resolution and context initialization sit INSIDE the try alongside execution. Neither
        // is expected to throw — the scope is fresh and IPlanExecutor is ValidateOnBuild-checked — but
        // IPlanRunExecutor promises the caller a stable error code and "never raw exception text"
        // without qualification. A guarantee that silently depends on unstated preconditions is worth
        // less than one the code enforces literally, so the try covers every statement that could
        // break it.
        try
        {
            // Identity first, envelope second — the governor fails closed on identity-less enveloped
            // tool calls, so the scoped execution context must carry the caller's identity before any
            // step can observe the envelope.
            var executionContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
            executionContext.Initialize(request.AgentId, runScope, turnNumber: 1);

            var planExecutor = scope.ServiceProvider.GetRequiredService<IPlanExecutor>();

            using (CapabilityEnvelopeAccessor.Begin(request.Envelope))
                return await planExecutor.ExecuteAsync(request.PlanId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Full detail stays in the structured log; the caller sees only a stable code so
            // infrastructure exception text (paths, connection strings) can never leak out.
            _logger.LogError(ex, "Plan run {PlanId} threw during enveloped setup or execution", request.PlanId);
            return Result<PlanExecutionSummary>.Fail("plan_run.execution_failed");
        }
        finally
        {
            // This run owns its budget entry — no conversation handler releases it, which is the
            // whole reason it exists as a separate key. Freed on every exit path; releasing a key that
            // a failed setup never created is a no-op remove.
            _conversationBudget.Release(PlanRunKeys.RunBudgetKey(runScope));
        }
    }
}
