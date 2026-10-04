using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Sets up a child governance scope: initialises its <see cref="IAgentExecutionContext"/> (inheriting
/// or minting the conversation id, call-once scope, turn number and workload identity per a
/// <see cref="GovernanceArmingPolicy"/>) and, when asked, resolves and resets its
/// <see cref="IToolCallAdmissionPipeline"/>.
/// </summary>
/// <remarks>
/// <para>
/// This sequence used to live in three independent copies — direct tool invocation, sub-plans and
/// delegation — that had already drifted (sub-plans did not inherit workload identity and treated an
/// empty conversation id as valid). The fixed order below is load-bearing: the context is initialised
/// first (it publishes the turn's governance attribution), then identity, then the pipeline.
/// </para>
/// <para>
/// <strong>Deliberately does not own the DI scope</strong> — creation and disposal differ per caller
/// (synchronous vs asynchronous, caller-owned vs method-owned) — <strong>and does not publish any
/// ambient accessor</strong>: that is <see cref="ArmedGovernance.Activate"/>, called around the work
/// itself. Callers that also publish other ambient state (a capability envelope) do so themselves.
/// </para>
/// <para>
/// <strong>Deliberately not callers:</strong> sites that own the request scope rather than opening a
/// child one — <c>AgentContextPropagationBehavior</c> (seeds the request's context),
/// <c>ExecuteAgentTurnCommandHandler</c>, <c>RunOrchestratedTaskCommandHandler</c>,
/// <c>AgentEvaluationService</c> and the FoundryHost middleware — and <c>PlanRunExecutor</c>, which
/// mints a fresh scope but arms the capability envelope and calls the scoped pipeline directly. A
/// Magentic participant (#769) is the intended next caller. Whether <c>PlanRunExecutor</c> should
/// propagate workload identity is an open question, not settled by this helper.
/// </para>
/// </remarks>
public static class GovernanceArmer
{
    /// <summary>
    /// Arms <paramref name="childServices"/>' execution context for <paramref name="agentId"/>.
    /// </summary>
    /// <param name="childServices">The child scope's service provider.</param>
    /// <param name="agentId">The agent the child governs as: the parent's own (same principal), the
    /// delegate's, or a synthetic caller identity, depending on the surface.</param>
    /// <param name="policy">Which values are inherited from <paramref name="parent"/> and which minted.</param>
    /// <param name="parent">The parent turn's context, or <see langword="null"/> when there is none to
    /// inherit from (a direct invocation, or a delegation run outside any governed turn).</param>
    /// <param name="fallbackScopeId">Used where the policy inherits but the parent supplies nothing.
    /// Required whenever the policy inherits the conversation id or falls back for call-once scope.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="fallbackScopeId"/> is needed by <paramref name="policy"/> but missing.
    /// </exception>
    public static ArmedGovernance Arm(
        IServiceProvider childServices,
        string agentId,
        GovernanceArmingPolicy policy,
        IAgentExecutionContext? parent = null,
        string? fallbackScopeId = null)
    {
        ArgumentNullException.ThrowIfNull(childServices);
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(policy);

        var context = childServices.GetRequiredService<IAgentExecutionContext>();
        context.Initialize(
            agentId,
            ResolveConversationId(policy, parent, fallbackScopeId),
            policy.InheritTurnNumber ? parent?.TurnNumber ?? 1 : 1,
            ResolveCallOnceScope(policy, parent, fallbackScopeId));

        if (policy.PropagateWorkloadIdentity && parent?.AgentIdentity is { } workloadIdentity)
            context.SetIdentity(workloadIdentity);

        // Required, not optional: the chain is registered unconditionally, and an absent one is
        // indistinguishable at runtime from a host whose gates are all off — tolerating null here would
        // let a broken composition run this path silently unguarded.
        IToolCallAdmissionPipeline? pipeline = null;
        if (policy.ResolvePipeline)
        {
            // Reset, not just resolved fresh: harmless today (a new scope can only resolve a pipeline
            // already in default state) but explicit, so no call site starts relying on that invariant.
            pipeline = childServices.GetRequiredService<IToolCallAdmissionPipeline>();
            pipeline.Reset();
        }

        return new ArmedGovernance(pipeline);
    }

    private static string ResolveConversationId(
        GovernanceArmingPolicy policy, IAgentExecutionContext? parent, string? fallbackScopeId)
    {
        if (policy.MintConversationId)
            return Guid.NewGuid().ToString();

        // Empty counts as missing, not just null: IAgentExecutionContext enforces no non-empty invariant,
        // and an empty id inherited as-is would pool unrelated children under one key.
        return string.IsNullOrEmpty(parent?.ConversationId)
            ? RequireFallback(fallbackScopeId, nameof(GovernanceArmingPolicy.MintConversationId))
            : parent.ConversationId;
    }

    private static string? ResolveCallOnceScope(
        GovernanceArmingPolicy policy, IAgentExecutionContext? parent, string? fallbackScopeId)
        => policy.CallOnceScope switch
        {
            CallOnceScopeSource.Omit => null,
            CallOnceScopeSource.InheritAsIs => parent?.CallOnceScopeId,
            CallOnceScopeSource.InheritOrFallback => string.IsNullOrEmpty(parent?.CallOnceScopeId)
                ? RequireFallback(fallbackScopeId, nameof(GovernanceArmingPolicy.CallOnceScope))
                : parent.CallOnceScopeId,
            _ => throw new ArgumentOutOfRangeException(
                nameof(policy), policy.CallOnceScope, "Unknown call-once scope source."),
        };

    private static string RequireFallback(string? fallbackScopeId, string policyMember)
        => string.IsNullOrEmpty(fallbackScopeId)
            ? throw new ArgumentException(
                $"A fallback scope id is required: the policy's {policyMember} inherits from the parent, "
                + "which may supply nothing.",
                nameof(fallbackScopeId))
            : fallbackScopeId;
}
