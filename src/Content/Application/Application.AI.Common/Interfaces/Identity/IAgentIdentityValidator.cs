using Domain.AI.Identity;

namespace Application.AI.Common.Interfaces.Identity;

/// <summary>
/// Validates whether an <see cref="AgentIdentity"/> is permitted to invoke a
/// specific tool. Together with <c>IKnowledgeScopeValidator</c> (the human-caller
/// scope check), forms the two-axis RBAC the harness applies at every tool call:
/// "did the human initiate work in this tenant?" AND "is this agent allowed this
/// tool?".
/// </summary>
/// <remarks>
/// <para>
/// Fail closed: missing policy is treated as deny, not allow. An unregistered tool
/// key returns <c>false</c>; an identity with <see cref="AgentIdentityKind.Unspecified"/>
/// returns <c>false</c>. This matches <see cref="IKnowledgeScopeValidator"/>'s
/// behaviour and mirrors the global guidance — security defaults must be the correct
/// defaults.
/// </para>
/// <para>
/// Implementations SHOULD emit a structured log entry on every deny so an operator
/// can distinguish "policy denied X" from "no policy for X" during incident triage
/// — the public boolean conflates them by design (a caller doesn't need to know
/// why an action was rejected, only that it was), but the audit trail must not.
/// </para>
/// </remarks>
public interface IAgentIdentityValidator
{
    /// <summary>
    /// Decides whether the given <paramref name="identity"/> is allowed to invoke
    /// the tool registered under <paramref name="toolKey"/>.
    /// </summary>
    /// <param name="identity">The agent identity making the call.</param>
    /// <param name="toolKey">The keyed-DI tool name (e.g. <c>"file_system"</c>).</param>
    /// <returns>
    /// <c>true</c> when the call is permitted; <c>false</c> when the identity is
    /// not permitted to invoke this tool, when no policy is registered for this
    /// tool, or when the identity is unspecified.
    /// </returns>
    bool CanInvoke(AgentIdentity identity, string toolKey);
}
