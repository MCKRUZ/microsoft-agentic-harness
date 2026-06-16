using Domain.AI.Planner;
using Xunit;

namespace Domain.AI.Tests.Planner;

/// <summary>
/// Regression coverage for the solution-review finding that <see cref="HumanGateConfig"/>
/// silently defaulted <see cref="HumanGateConfig.Approvers"/> to the placeholder identity
/// <c>"default-approver"</c>. That non-empty default masked a forgotten approver list:
/// the resulting escalation matched no real approver in the pending-escalation filter, so the
/// gate became an unapprovable dead gate that stalled the plan until timeout rather than
/// failing validation loudly. The fix replaces the placeholder with an empty list, an honest
/// sentinel a validator can reject at construction/validation time.
/// </summary>
public sealed class HumanGateConfigSolutionReviewFixTests
{
    [Fact]
    public void Approvers_WhenNotSet_DefaultsToEmptyNotPlaceholderIdentity()
    {
        var config = new HumanGateConfig
        {
            EscalationMessage = "Please approve",
            ApprovalStrategy = ApprovalStrategy.AnyOf
        };

        Assert.Empty(config.Approvers);
        Assert.DoesNotContain("default-approver", config.Approvers);
    }

    [Fact]
    public void Approvers_WhenExplicitlySet_PreservesSuppliedIdentities()
    {
        var config = new HumanGateConfig
        {
            EscalationMessage = "Please approve",
            ApprovalStrategy = ApprovalStrategy.AllOf,
            Approvers = ["alice", "bob"]
        };

        Assert.Equal(["alice", "bob"], config.Approvers);
    }
}
