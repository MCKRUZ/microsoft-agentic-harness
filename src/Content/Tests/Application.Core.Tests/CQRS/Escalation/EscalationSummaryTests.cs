using Application.Core.CQRS.Escalation;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS.Escalation;

/// <summary>
/// Tests for <see cref="EscalationSummary.FromRequest"/> — the wire-safe projection an approver's
/// dashboard actually reads, including the #325 retry-attribution fields.
/// </summary>
public sealed class EscalationSummaryTests
{
    private static EscalationRequest CreateRequest(
        int attemptNumber = 1, string? priorFailureReason = null, Guid? predecessorEscalationId = null) =>
        new()
        {
            EscalationId = Guid.NewGuid(),
            AgentId = "agent-1",
            ToolName = "file_system",
            Arguments = new Dictionary<string, string>(),
            Description = "test escalation",
            RiskLevel = RiskLevel.High,
            Priority = EscalationPriority.Blocking,
            Approvers = ["alice"],
            RequestedAt = DateTimeOffset.UtcNow,
            AttemptNumber = attemptNumber,
            PriorFailureReason = priorFailureReason,
            PredecessorEscalationId = predecessorEscalationId
        };

    [Fact]
    public void FromRequest_FirstAttempt_CarriesNoRetryAttribution()
    {
        var summary = EscalationSummary.FromRequest(CreateRequest());

        summary.AttemptNumber.Should().Be(1);
        summary.PriorFailureReason.Should().BeNull();
        summary.PredecessorEscalationId.Should().BeNull();
    }

    [Fact]
    public void FromRequest_RetryAttempt_CarriesTheRetryAttributionCard()
    {
        // This is the field set the whole #325 feature exists to surface — an approver reading
        // only EscalationSummary (the list and detail read surfaces) must see it, not just the
        // real-time notification channels.
        var predecessorId = Guid.NewGuid();
        var request = CreateRequest(attemptNumber: 2, priorFailureReason: "permission denied", predecessorEscalationId: predecessorId);

        var summary = EscalationSummary.FromRequest(request);

        summary.AttemptNumber.Should().Be(2);
        summary.PriorFailureReason.Should().Be("permission denied");
        summary.PredecessorEscalationId.Should().Be(predecessorId);
    }

    [Fact]
    public void FromRequest_NullRequest_Throws()
    {
        var act = () => EscalationSummary.FromRequest(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
