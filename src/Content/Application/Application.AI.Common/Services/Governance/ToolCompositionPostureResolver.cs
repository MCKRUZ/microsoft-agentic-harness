using Domain.AI.Governance;
using Domain.Common.Config.AI.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// The single place that maps a (source capability, sink capability) pairing to its configured
/// posture. Used identically by <see cref="ToolCompositionReporter"/> (to decide what to report at
/// build time) and by <c>ToolInvocationGovernor.RequiresApprovalForToolComposition</c> (to decide
/// whether to gate at call time) — both against their own live config snapshot, at their own moment.
/// </summary>
/// <remarks>
/// A second copy of this lookup is exactly how the two callers would drift: one reading a stale
/// snapshot the other doesn't, or one applying a default the other doesn't. There is deliberately one
/// implementation.
/// </remarks>
public static class ToolCompositionPostureResolver
{
    /// <summary>
    /// Resolves the posture for one (source, sink) capability pairing: the first matching entry in
    /// <see cref="ToolCompositionGatingConfig.Pairings"/>, or <see cref="ToolCompositionGatingConfig.DefaultPosture"/>
    /// when nothing matches.
    /// </summary>
    public static CompositionPosture Resolve(ToolCompositionGatingConfig gating, ToolCompositionCapability source, ToolCompositionCapability sink)
    {
        ArgumentNullException.ThrowIfNull(gating);

        foreach (var pairing in gating.Pairings)
        {
            if (pairing.Source == source && pairing.Sink == sink)
                return pairing.Posture;
        }

        return gating.DefaultPosture;
    }
}
