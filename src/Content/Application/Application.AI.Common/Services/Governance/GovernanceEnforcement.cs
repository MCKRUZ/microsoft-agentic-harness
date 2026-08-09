using Domain.Common.Config.AI;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// The single answer to "is per-invocation tool governance active for the flow running right now?" —
/// the switch that decides whether the governor engages and whether a turn is reported as governed.
/// </summary>
/// <remarks>
/// <para>
/// A plain predicate rather than an injected service: it is one expression over configuration and
/// ambient state, not a seam a consumer replaces, and wrapping it in a registered type bought nothing
/// but ceremony. It exists as a named thing at all because it has more than one caller —
/// <see cref="ToolInvocationGovernor"/> asks it whether to run, and
/// <see cref="GovernanceTraceRecorder"/> asks it whether the turn was governed — and a rule this
/// consequential held in two copies is how the copies drift.
/// </para>
/// <para>
/// <strong>The answer is live, not sticky.</strong> A bundle run arms enforcement for the duration of
/// its ambient capability envelope, and it goes away when that scope disposes. Callers that need the
/// sticky form — "was this turn ever governed?" — must read
/// <see cref="Interfaces.Governance.IGovernanceTraceRecorder.EnforcementEnabled"/>, which folds this
/// together with what was observed while authorizing. Using the sticky form to decide whether to
/// enforce would keep a bundle's enforcement armed after its run had ended.
/// </para>
/// </remarks>
public static class GovernanceEnforcement
{
    /// <summary>
    /// Whether per-invocation enforcement is active for the current flow: the host opted in globally
    /// via <c>GovernanceConfig.EnforceToolInvocation</c>, <em>or</em> a bundle run is in progress.
    /// </summary>
    /// <param name="governance">The live governance configuration.</param>
    /// <returns><see langword="true"/> when the governor must engage.</returns>
    /// <remarks>
    /// A bundle executes an externally-authored agent, so its whole flow must be governed and must fail
    /// closed. The presence of a per-caller <c>CapabilityEnvelope</c> is the single ambient fact that
    /// is derived from, which means there is no way to publish an envelope without also arming the
    /// governor.
    /// </remarks>
    public static bool IsActive(GovernanceConfig governance)
    {
        ArgumentNullException.ThrowIfNull(governance);

        return governance.EnforceToolInvocation || CapabilityEnvelopeAccessor.Current is not null;
    }
}
