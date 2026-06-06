using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;

namespace Infrastructure.AI.Changes;

/// <summary>
/// Default <see cref="IChangeProposalGateResolver"/>: maps every supported
/// target kind to the standard four-gate pipeline. Critical blast radius is
/// guaranteed to include the approval gate even if the target's default would
/// otherwise omit it — the resolver is the right place to enforce
/// "Critical always needs approval" because the resulting list is frozen into
/// the proposal at submission time and cannot be lowered later.
/// </summary>
/// <remarks>
/// Consumers needing stricter pipelines (an extra <c>compliance</c> gate for
/// regulated environments, a <c>cost</c> gate for IaC at high blast radius)
/// register their own <see cref="IChangeProposalGateResolver"/> implementation
/// in place of this one.
/// </remarks>
public sealed class DefaultChangeProposalGateResolver : IChangeProposalGateResolver
{
    private static readonly IReadOnlyList<string> StandardPipeline =
    [
        WellKnownGateKeys.SelfValidation,
        WellKnownGateKeys.Policy,
        WellKnownGateKeys.Approval,
        WellKnownGateKeys.Merge
    ];

    private static readonly IReadOnlyList<string> AutoApprovePipeline =
    [
        WellKnownGateKeys.SelfValidation,
        WellKnownGateKeys.Policy,
        WellKnownGateKeys.Merge
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> Resolve(ChangeTargetKind targetKind, BlastRadius blastRadius)
    {
        // Trivial blast radius (cosmetic / comment-only) may auto-approve.
        // Anything Medium or higher always passes through approval.
        // Low is a judgment call — default to requiring approval; consumers
        // who want auto-approve at Low override this resolver.
        if (blastRadius == BlastRadius.Trivial)
        {
            return AutoApprovePipeline;
        }

        return StandardPipeline;
    }
}
