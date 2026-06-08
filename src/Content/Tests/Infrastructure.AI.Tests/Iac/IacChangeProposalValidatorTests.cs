using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.Iac;
using Domain.AI.Changes;
using Domain.AI.Iac;
using Domain.AI.Identity;
using Domain.AI.SkillTraining;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.Iac;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using GateAction = Domain.AI.Changes.GateAction;

namespace Infrastructure.AI.Tests.Iac;

/// <summary>
/// Unit tests for <see cref="IacChangeProposalValidator"/>. Each scenario builds a
/// service provider with (or without) a keyed <see cref="IIacGenerator"/> and a
/// proposal whose target is (or is not) an <see cref="IacDeploymentTarget"/>, then
/// asserts the resulting <see cref="GateResult"/>.
/// </summary>
public sealed class IacChangeProposalValidatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-08T12:00:00Z");

    private static GateContext Context() => new()
    {
        Mode = OrchestratorMode.Shadow,
        AttemptCount = 1,
        EvaluatedAt = Now,
        CorrelationId = "corr-1"
    };

    private static ChangeProposal IacProposal(string backend = "terraform", string modulePath = "modules/network/main.tf")
        => new()
        {
            Id = "cp-iac-1",
            Target = new IacDeploymentTarget(backend, "net", modulePath, "prod"),
            Diff = [new ChangeEdit { Op = EditOp.Replace, Target = modulePath, Content = "# x" }],
            BlastRadius = BlastRadius.Medium,
            RequiredGates = [],
            Status = ChangeProposalStatus.Draft,
            SubmittedBy = new AgentIdentity { Id = "a-1", Kind = AgentIdentityKind.Development },
            SubmittedAt = Now
        };

    private static ChangeProposal GitProposal() => new()
    {
        Id = "cp-git-1",
        Target = new GitRepoTarget("https://github.com/example/x.git", "main"),
        Diff = [new ChangeEdit { Op = EditOp.Replace, Target = "a.cs", Content = "// x" }],
        BlastRadius = BlastRadius.Low,
        RequiredGates = [],
        Status = ChangeProposalStatus.Draft,
        SubmittedBy = new AgentIdentity { Id = "a-1", Kind = AgentIdentityKind.Development },
        SubmittedAt = Now
    };

    private static IServiceProvider ProviderWith(string backendKey, IIacGenerator generator)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(backendKey, generator);
        return services.BuildServiceProvider();
    }

    private static IacChangeProposalValidator Validator(IServiceProvider sp)
        => new(sp, NullLogger<IacChangeProposalValidator>.Instance);

    private static Mock<IIacGenerator> GeneratorReturning(IacPlanResult plan, IacScanResult? scan = null)
    {
        var mock = new Mock<IIacGenerator>();
        mock.SetupGet(g => g.Backend).Returns(IacBackend.Terraform);
        mock.Setup(g => g.PlanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IacPlanResult>.Success(plan));
        if (scan is not null)
        {
            mock.Setup(g => g.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<IacScanResult>.Success(scan));
        }

        return mock;
    }

    private static IacPlanResult Plan(bool succeeded, bool destructive = false) => new()
    {
        Backend = IacBackend.Terraform,
        ModulePath = "modules/network",
        Succeeded = succeeded,
        HasDestructiveChanges = destructive,
        Summary = "1 to add"
    };

    private static IacScanResult Scan(bool passed) => new()
    {
        Backend = IacBackend.Terraform,
        ModulePath = "modules/network",
        Passed = passed,
        ScannersRun = ["checkov", "tfsec"],
        Findings = passed ? [] : [new IacScanFinding { Scanner = "checkov", RuleId = "CKV_X", Severity = IacScanSeverity.High }]
    };

    [Fact]
    public void Key_IsIacPlanScan()
    {
        Validator(new ServiceCollection().BuildServiceProvider()).Key.Should().Be("iac_plan_scan");
    }

    [Fact]
    public async Task ValidateAsync_NonIacTarget_Passes()
    {
        var sut = Validator(new ServiceCollection().BuildServiceProvider());

        var result = await sut.ValidateAsync(GitProposal(), Context(), CancellationToken.None);

        result.Action.Should().Be(GateAction.Pass);
    }

    [Fact]
    public async Task ValidateAsync_NoGeneratorForBackend_Fails()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var sut = Validator(sp);

        var result = await sut.ValidateAsync(IacProposal("pulumi"), Context(), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        result.Reason.Should().Contain("iac.backend_not_registered");
    }

    [Fact]
    public async Task ValidateAsync_PlanFails_Fails()
    {
        var generator = new Mock<IIacGenerator>();
        generator.SetupGet(g => g.Backend).Returns(IacBackend.Terraform);
        generator.Setup(g => g.PlanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IacPlanResult>.Fail("iac.plan.sandbox_error"));
        var sut = Validator(ProviderWith("terraform", generator.Object));

        var result = await sut.ValidateAsync(IacProposal(), Context(), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        result.Reason.Should().Contain("iac.plan_failed");
    }

    [Fact]
    public async Task ValidateAsync_PlanInvalid_Fails()
    {
        var generator = GeneratorReturning(Plan(succeeded: false));
        var sut = Validator(ProviderWith("terraform", generator.Object));

        var result = await sut.ValidateAsync(IacProposal(), Context(), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        result.Reason.Should().Contain("iac.plan_invalid");
    }

    [Fact]
    public async Task ValidateAsync_DestructivePlan_Fails()
    {
        var generator = GeneratorReturning(Plan(succeeded: true, destructive: true));
        var sut = Validator(ProviderWith("terraform", generator.Object));

        var result = await sut.ValidateAsync(IacProposal(), Context(), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        result.Reason.Should().Contain("iac.plan_destructive");
    }

    [Fact]
    public async Task ValidateAsync_ScanBlocked_Fails()
    {
        var generator = GeneratorReturning(Plan(succeeded: true), Scan(passed: false));
        var sut = Validator(ProviderWith("terraform", generator.Object));

        var result = await sut.ValidateAsync(IacProposal(), Context(), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        result.Reason.Should().Contain("iac.scan_blocked");
    }

    [Fact]
    public async Task ValidateAsync_CleanPlanAndScan_Passes()
    {
        var generator = GeneratorReturning(Plan(succeeded: true), Scan(passed: true));
        var sut = Validator(ProviderWith("terraform", generator.Object));

        var result = await sut.ValidateAsync(IacProposal(), Context(), CancellationToken.None);

        result.Action.Should().Be(GateAction.Pass);
    }

    [Fact]
    public async Task ValidateAsync_ScanFails_Fails()
    {
        var generator = new Mock<IIacGenerator>();
        generator.SetupGet(g => g.Backend).Returns(IacBackend.Terraform);
        generator.Setup(g => g.PlanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IacPlanResult>.Success(Plan(succeeded: true)));
        generator.Setup(g => g.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IacScanResult>.Fail("iac.scan.sandbox_error"));
        var sut = Validator(ProviderWith("terraform", generator.Object));

        var result = await sut.ValidateAsync(IacProposal(), Context(), CancellationToken.None);

        result.Action.Should().Be(GateAction.Fail);
        result.Reason.Should().Contain("iac.scan_failed");
    }
}
