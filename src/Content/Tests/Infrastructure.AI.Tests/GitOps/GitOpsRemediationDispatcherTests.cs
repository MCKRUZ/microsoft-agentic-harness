using Application.AI.Common.CQRS.Changes.SubmitChangeProposal;
using Domain.AI.Changes;
using Domain.AI.GitOps;
using Domain.AI.Identity;
using Domain.AI.SkillTraining;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.GitOps;
using Infrastructure.AI.Tests.Support;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.GitOps;

/// <summary>
/// Tests <see cref="GitOpsRemediationDispatcher"/> against a mocked
/// <see cref="IMediator"/>. Verifies the plan→command mapping, the
/// unconditional <c>IsStateChange</c> flag, success pass-through, and failure
/// surfacing.
/// </summary>
public sealed class GitOpsRemediationDispatcherTests
{
    private readonly Mock<IMediator> _mediator = new();

    private GitOpsRemediationDispatcher CreateSut()
        => new(TestScopeFactory.For(_mediator.Object), NullLogger<GitOpsRemediationDispatcher>.Instance);

    private static RemediationPlan ValidPlan(
        GitOpsControllerKind kind = GitOpsControllerKind.Flux,
        BlastRadius blastRadius = BlastRadius.Medium)
    {
        var drift = new DriftReport
        {
            ControllerKind = kind,
            CapturedAt = DateTimeOffset.Parse("2026-06-08T12:00:00Z"),
            DriftedResources =
            [
                new DriftedResource
                {
                    ApiVersion = "v1", Kind = "Kustomization", Name = "apps",
                    Namespace = "flux-system", DesiredPath = "./apps"
                }
            ]
        };

        return new RemediationPlan
        {
            ControllerKind = kind,
            SourceDrift = drift,
            Target = new GitRepoTarget("https://github.com/example/cluster-config.git", "main"),
            Edits = [new ChangeEdit { Op = EditOp.Replace, Target = "./apps", Content = "# fix" }],
            ProposedBlastRadius = blastRadius,
            Summary = "Flux: reconverge 1 drifted resource(s)."
        };
    }

    private static ChangeProposal SampleProposal() => new()
    {
        Id = "cp-1",
        Target = new GitRepoTarget("https://github.com/example/cluster-config.git", "main"),
        Diff = [new ChangeEdit { Op = EditOp.Replace, Target = "./apps", Content = "# fix" }],
        BlastRadius = BlastRadius.Medium,
        RequiredGates = [],
        Status = ChangeProposalStatus.Draft,
        SubmittedBy = new AgentIdentity { Id = "agent-001", Kind = AgentIdentityKind.Development },
        SubmittedAt = DateTimeOffset.Parse("2026-06-08T12:00:00Z")
    };

    [Fact]
    public async Task DispatchAsync_ValidPlan_MapsToCommandAndReturnsSuccess()
    {
        SubmitChangeProposalCommand? captured = null;
        _mediator
            .Setup(m => m.Send(It.IsAny<SubmitChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ChangeProposal>>, CancellationToken>((cmd, _) => captured = (SubmitChangeProposalCommand)cmd)
            .ReturnsAsync(Result<ChangeProposal>.Success(SampleProposal()));
        var sut = CreateSut();

        var result = await sut.DispatchAsync(ValidPlan(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("cp-1");
        captured.Should().NotBeNull();
        captured!.Diff.Should().ContainSingle(e => e.Target == "./apps");
        captured.BlastRadius.Should().Be(BlastRadius.Medium);
        captured.Summary.Should().Be("Flux: reconverge 1 drifted resource(s).");
        captured.SkillKey.Should().Be("gitops:flux");
    }

    [Fact]
    public async Task DispatchAsync_AlwaysMarksCommandAsStateChange()
    {
        SubmitChangeProposalCommand? captured = null;
        _mediator
            .Setup(m => m.Send(It.IsAny<SubmitChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ChangeProposal>>, CancellationToken>((cmd, _) => captured = (SubmitChangeProposalCommand)cmd)
            .ReturnsAsync(Result<ChangeProposal>.Success(SampleProposal()));
        var sut = CreateSut();

        await sut.DispatchAsync(ValidPlan(), CancellationToken.None);

        captured!.IsStateChange.Should().BeTrue();
    }

    [Fact]
    public async Task DispatchAsync_ArgoCdPlan_DerivesArgoCdSkillKey()
    {
        SubmitChangeProposalCommand? captured = null;
        _mediator
            .Setup(m => m.Send(It.IsAny<SubmitChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ChangeProposal>>, CancellationToken>((cmd, _) => captured = (SubmitChangeProposalCommand)cmd)
            .ReturnsAsync(Result<ChangeProposal>.Success(SampleProposal()));
        var sut = CreateSut();

        await sut.DispatchAsync(ValidPlan(GitOpsControllerKind.ArgoCd), CancellationToken.None);

        captured!.SkillKey.Should().Be("gitops:argocd");
    }

    [Fact]
    public async Task DispatchAsync_EmptyPlan_ReturnsFailWithoutCallingMediator()
    {
        var plan = ValidPlan() with { Edits = [] };
        var sut = CreateSut();

        var result = await sut.DispatchAsync(plan, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("gitops.remediation.empty_plan");
        _mediator.Verify(m => m.Send(It.IsAny<SubmitChangeProposalCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_MediatorReturnsFailure_PassesFailureThrough()
    {
        _mediator
            .Setup(m => m.Send(It.IsAny<SubmitChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ChangeProposal>.Fail("changes.validation.bad_target"));
        var sut = CreateSut();

        var result = await sut.DispatchAsync(ValidPlan(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("changes.validation.bad_target");
    }

    [Fact]
    public async Task DispatchAsync_MediatorThrows_ReturnsDispatchFailedCode()
    {
        _mediator
            .Setup(m => m.Send(It.IsAny<SubmitChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("pipeline blew up"));
        var sut = CreateSut();

        var result = await sut.DispatchAsync(ValidPlan(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("gitops.remediation.dispatch_failed");
    }
}
