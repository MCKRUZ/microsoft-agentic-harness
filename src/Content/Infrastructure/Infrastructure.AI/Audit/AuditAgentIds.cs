using Application.AI.Common.Interfaces.Audit;
using Application.AI.Common.Services.Tools;

namespace Infrastructure.AI.Audit;

/// <summary>
/// The agent id an audit trail records (#903): a configured agent's name as it is, and a direct invocation's
/// <c>direct-invoke:{user id}</c> with the user id replaced by its audit reference.
/// </summary>
/// <remarks>
/// One mapping for every trail that records an agent, so the same caller has the same recorded id in all of them and a
/// query by a raw id can be mapped the same way.
/// </remarks>
public static class AuditAgentIds
{
    /// <summary>Returns the agent id to record for <paramref name="agentId"/>.</summary>
    /// <param name="agentId">The agent id as the running system knows it.</param>
    /// <param name="pseudonymizer">Makes the caller's reference.</param>
    /// <returns><paramref name="agentId"/>, or the direct-invocation id with its caller as a reference.</returns>
    public static string Recorded(string agentId, IAuditPseudonymizer pseudonymizer)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(pseudonymizer);
        return DirectInvocationAgentIds.TryGetOwner(agentId, out var owner)
            ? DirectInvocationAgentIds.For(pseudonymizer.Person(owner))
            : agentId;
    }
}
