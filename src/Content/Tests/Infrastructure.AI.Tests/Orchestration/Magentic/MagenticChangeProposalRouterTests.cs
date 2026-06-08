using Application.AI.Common.CQRS.Changes.SubmitChangeProposal;
using Domain.AI.Changes;
using Domain.AI.Identity;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.Orchestration.Magentic;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// PR-6 acceptance tests for the replan → ChangeProposal router. Verifies
/// that state-change replan text submits a draft proposal via
/// <see cref="SubmitChangeProposalCommand"/>, and non-state-change replans
/// are dropped without dispatch.
/// </summary>
public sealed class MagenticChangeProposalRouterTests
{
    [Fact]
    public async Task State_change_replan_submits_change_proposal()
    {
        var mediator = new Mock<IMediator>();
        SubmitChangeProposalCommand? captured = null;
        mediator.Setup(m => m.Send(It.IsAny<SubmitChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((cmd, _) => captured = (SubmitChangeProposalCommand)cmd)
            .ReturnsAsync((object cmd, CancellationToken _) => Result<ChangeProposal>.Success(StubProposal((SubmitChangeProposalCommand)cmd)));

        var router = new MagenticChangeProposalRouter(mediator.Object, NullLogger<MagenticChangeProposalRouter>.Instance);

        var proposal = await router.TryRouteAsync(new MagenticReplanInfo
        {
            WorkflowId = Guid.NewGuid(),
            WorkflowName = "wf",
            PlanVersion = 2,
            ReplanText = "Now apply the migration and deploy."
        }, CancellationToken.None);

        proposal.Should().NotBeNull();
        captured.Should().NotBeNull();
        captured!.IsStateChange.Should().BeTrue();
        captured.Summary.Should().Contain("Magentic replan v2");
        captured.Target.Should().BeOfType<GitRepoTarget>();
    }

    [Fact]
    public async Task Non_state_change_replan_is_dropped()
    {
        var mediator = new Mock<IMediator>();
        var router = new MagenticChangeProposalRouter(mediator.Object, NullLogger<MagenticChangeProposalRouter>.Instance);

        var proposal = await router.TryRouteAsync(new MagenticReplanInfo
        {
            WorkflowId = Guid.NewGuid(),
            WorkflowName = "wf",
            PlanVersion = 2,
            ReplanText = "Re-read documentation and summarize findings."
        }, CancellationToken.None);

        proposal.Should().BeNull();
        mediator.Verify(m => m.Send(It.IsAny<SubmitChangeProposalCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ChangeProposal StubProposal(SubmitChangeProposalCommand cmd) => new()
    {
        Id = "test-id",
        Summary = cmd.Summary,
        Target = cmd.Target,
        Diff = cmd.Diff,
        BlastRadius = cmd.BlastRadius,
        RequiredGates = new[] { "validation" },
        Status = ChangeProposalStatus.Draft,
        SubmittedBy = new AgentIdentity { Id = "agent", Kind = AgentIdentityKind.Development },
        SubmittedAt = DateTimeOffset.UtcNow
    };
}
