using Domain.Common.Config.AI;

namespace Application.AI.Common.Factories;

/// <summary>
/// Central eligibility rule deciding whether an agent execution context may be served by the
/// composed resilient chat client (the <c>ResilienceConfig.FallbackChain</c>) instead of its
/// raw per-context provider client. Shared by the stash side
/// (<see cref="AgentExecutionContextFactory"/>) and the consume side (<see cref="AgentFactory"/>)
/// so the two gates can never disagree.
/// </summary>
/// <remarks>
/// The resilient client always talks to the configured fallback chain — it cannot honor a
/// per-context provider or deployment choice. Substituting it is therefore only safe when the
/// context resolved to exactly the primary configured provider and deployment:
/// <list type="bullet">
///   <item><description>the resolved framework equals <c>AppConfig.AI.AgentFramework.ClientType</c>
///   (no per-skill or per-options framework override to a different provider);</description></item>
///   <item><description>the framework is an <c>IChatClient</c>-style provider — never
///   <see cref="AIAgentFrameworkClientType.PersistentAgents"/> (its deployment slot carries a
///   provisioned AgentId that the fallback chain would silently abandon), never
///   <see cref="AIAgentFrameworkClientType.FoundryResponses"/> (built via
///   <c>IFoundryAgentProvider</c>, not the chat-client path), never
///   <see cref="AIAgentFrameworkClientType.Echo"/> (deterministic test client);</description></item>
///   <item><description>the resolved deployment equals the configured
///   <c>DefaultDeployment</c> (no per-skill <c>ModelOverride</c> or options override).</description></item>
/// </list>
/// Contexts failing any check keep their raw client untouched — explicit per-context choices
/// always win over the generic resilience chain.
/// </remarks>
internal static class ResilientClientEligibility
{
    /// <summary>
    /// Returns <see langword="true"/> when a context resolved to the given framework and
    /// deployment may be transparently served by the resilient fallback-chain client.
    /// </summary>
    /// <param name="resolvedFramework">The framework type the context resolved to.</param>
    /// <param name="resolvedDeployment">
    /// The deployment the context resolved to (for <see cref="AIAgentFrameworkClientType.PersistentAgents"/>
    /// callers this slot carries the AgentId, which is excluded by the framework check anyway).
    /// </param>
    /// <param name="primaryConfig">The primary provider configuration (<c>AppConfig.AI.AgentFramework</c>).</param>
    public static bool IsEligible(
        AIAgentFrameworkClientType resolvedFramework,
        string? resolvedDeployment,
        AgentFrameworkConfig? primaryConfig)
    {
        if (primaryConfig is null)
            return false;

        // Non-IChatClient-style frameworks never route through the resilient client.
        if (resolvedFramework is AIAgentFrameworkClientType.PersistentAgents
            or AIAgentFrameworkClientType.FoundryResponses
            or AIAgentFrameworkClientType.Echo)
        {
            return false;
        }

        // A framework override to a different provider than the primary config must win.
        if (resolvedFramework != primaryConfig.ClientType)
            return false;

        // A deployment override differing from the configured default must win. Ordinal
        // comparison: any difference is treated as an explicit override (conservative).
        return string.IsNullOrEmpty(resolvedDeployment)
            || string.Equals(resolvedDeployment, primaryConfig.DefaultDeployment, StringComparison.Ordinal);
    }
}
