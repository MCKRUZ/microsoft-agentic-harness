using Domain.AI.Changes;

namespace Domain.AI.Governance;

/// <summary>
/// A single row in a tier-policy's per-blast-radius rule table. Maps one
/// <see cref="BlastRadius"/> to the <see cref="AutonomyDecision"/> the tier wants
/// to apply by default; <see cref="AllowAutoApproveForStateChange"/> opens the
/// state-changer escape hatch on a per-row basis.
/// </summary>
/// <param name="Radius">The blast radius this row applies to.</param>
/// <param name="Decision">
/// The decision for proposals at this radius. <see cref="AutonomyDecision.AutoApprove"/>
/// is the only value that takes effect without the state-change opt-in below — the
/// other two outcomes are always honoured.
/// </param>
/// <param name="AllowAutoApproveForStateChange">
/// When true and <paramref name="Decision"/> is <see cref="AutonomyDecision.AutoApprove"/>,
/// the tier permits auto-approval even when the proposal is a state-changer. False by
/// default: the hard invariant is that state-changers default to
/// <see cref="AutonomyDecision.RequiresApproval"/> regardless of tier, opt-in only.
/// </param>
public sealed record BlastRadiusAutonomyRule(
    BlastRadius Radius,
    AutonomyDecision Decision,
    bool AllowAutoApproveForStateChange = false);
