namespace Application.AI.Common.Services.Tools;

/// <summary>
/// The synthetic agent id a direct tool invocation runs under: <c>direct-invoke:</c> followed by the caller's user id. It
/// is the permission subject and the audit subject for the call.
/// </summary>
/// <remarks>
/// Shared so the audit trails can recognise one and record the caller as a pseudonymous reference rather than the user id
/// it carries (#903). No configured agent is named this way, since the prefix's colon is outside what an agent name
/// permits, so a direct invocation cannot pick up permission rules authored for a real agent.
/// </remarks>
public static class DirectInvocationAgentIds
{
    /// <summary>The prefix every direct-invocation agent id starts with.</summary>
    public const string Prefix = "direct-invoke:";

    /// <summary>Returns the agent id a direct invocation by <paramref name="ownerId"/> runs under.</summary>
    /// <param name="ownerId">The caller's user id.</param>
    /// <returns><see cref="Prefix"/> followed by <paramref name="ownerId"/>.</returns>
    public static string For(string ownerId) => Prefix + ownerId;

    /// <summary>Gets the caller's user id from a direct-invocation agent id.</summary>
    /// <param name="agentId">Any agent id.</param>
    /// <param name="ownerId">The caller's user id when <paramref name="agentId"/> is a direct-invocation id.</param>
    /// <returns>Whether <paramref name="agentId"/> is a direct-invocation id with a caller after the prefix.</returns>
    public static bool TryGetOwner(string agentId, out string ownerId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        if (agentId.StartsWith(Prefix, StringComparison.Ordinal) && agentId.Length > Prefix.Length)
        {
            ownerId = agentId[Prefix.Length..];
            return true;
        }

        ownerId = string.Empty;
        return false;
    }
}
