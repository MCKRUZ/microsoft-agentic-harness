using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Sets up a child governance scope: initialises its <see cref="IAgentExecutionContext"/> (inheriting
/// or minting the conversation id, call-once scope and turn number per a
/// <see cref="GovernanceArmingPolicy"/>, and carrying the parent's workload identity across) and, for
/// <see cref="ArmWithAdmission"/>, resolves and resets its <see cref="IToolCallAdmissionPipeline"/>.
/// </summary>
/// <remarks>
/// <para>
/// This sequence used to live in three independent copies — direct tool invocation, sub-plans and
/// delegation — that had already drifted (sub-plans did not inherit workload identity and treated an
/// empty conversation id as valid). The order is load-bearing: the context is initialised first (it
/// publishes the turn's governance attribution), then identity, then the pipeline.
/// </para>
/// <para>
/// <strong>Deliberately does not own the DI scope</strong> (creation and disposal differ per caller)
/// <strong>and does not publish any ambient accessor</strong> — callers wrap the work in
/// <see cref="ToolAdmissionAccessor.Begin(IToolCallAdmissionPipeline)"/> themselves. Sites that own
/// their request scope rather than opening a child one (the MediatR behaviours, the turn handlers,
/// <c>PlanRunExecutor</c>) do not use it.
/// </para>
/// </remarks>
public static class GovernanceArmer
{
    /// <summary>
    /// Initialises <paramref name="childServices"/>' execution context for <paramref name="agentId"/>,
    /// stamping the parent's workload identity onto it when the parent carries one.
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
    /// <paramref name="agentId"/> is blank, or <paramref name="fallbackScopeId"/> is needed by
    /// <paramref name="policy"/> but missing.
    /// </exception>
    public static void Arm(
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
            policy.MintConversationId
                ? Guid.NewGuid().ToString()
                : InheritOrFallback(parent?.ConversationId, fallbackScopeId, "conversation id"),
            policy.InheritTurnNumber ? parent?.TurnNumber ?? 1 : 1,
            ResolveCallOnceScope(policy, parent, fallbackScopeId));

        if (parent?.AgentIdentity is { } workloadIdentity)
            context.SetIdentity(workloadIdentity);
    }

    /// <summary>
    /// <see cref="Arm"/>, then resolves and resets the child scope's admission pipeline and returns it,
    /// ready to be published with <see cref="ToolAdmissionAccessor.Begin(IToolCallAdmissionPipeline)"/>.
    /// </summary>
    /// <returns>The child scope's admission pipeline, reset.</returns>
    public static IToolCallAdmissionPipeline ArmWithAdmission(
        IServiceProvider childServices,
        string agentId,
        GovernanceArmingPolicy policy,
        IAgentExecutionContext? parent = null,
        string? fallbackScopeId = null)
    {
        Arm(childServices, agentId, policy, parent, fallbackScopeId);

        // Required, not optional: the chain is registered unconditionally, and an absent one is
        // indistinguishable at runtime from a host whose gates are all off — tolerating null here would
        // let a broken composition run this path silently unguarded. Reset, not just resolved fresh:
        // harmless today (a new scope can only resolve a pipeline already in default state) but
        // explicit, so no call site starts relying on that invariant.
        var pipeline = childServices.GetRequiredService<IToolCallAdmissionPipeline>();
        pipeline.Reset();
        return pipeline;
    }

    private static string? ResolveCallOnceScope(
        GovernanceArmingPolicy policy, IAgentExecutionContext? parent, string? fallbackScopeId)
        => policy.CallOnceScope switch
        {
            CallOnceScopeSource.Omit => null,
            CallOnceScopeSource.InheritAsIs => parent?.CallOnceScopeId,
            CallOnceScopeSource.InheritOrFallback =>
                InheritOrFallback(parent?.CallOnceScopeId, fallbackScopeId, "call-once scope"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(policy), policy.CallOnceScope, "Unknown call-once scope source."),
        };

    // Empty counts as missing, not just null: IAgentExecutionContext enforces no non-empty invariant,
    // and an empty id inherited as-is would pool unrelated children under one key.
    private static string InheritOrFallback(string? parentValue, string? fallbackScopeId, string what)
    {
        if (!string.IsNullOrEmpty(parentValue))
            return parentValue;

        return string.IsNullOrEmpty(fallbackScopeId)
            ? throw new ArgumentException(
                $"A fallback scope id is required: this policy inherits the {what} from the parent, "
                + "which may supply nothing.",
                nameof(fallbackScopeId))
            : fallbackScopeId;
    }
}
