using Domain.AI.Iac;
using FluentAssertions;
using Infrastructure.AI.Iac;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Iac;

/// <summary>
/// Unit tests for <see cref="BicepGenerator"/>. Scaffold output is asserted
/// directly; plan (<c>bicep build</c>) and scan (ARM-TTK + Checkov) are exercised
/// against a recording sandbox so the exact CLI program + arguments are verified
/// and canned output is parsed into the domain result shapes. Never invokes a real CLI.
/// </summary>
public sealed class BicepGeneratorTests
{
    private static BicepGenerator Create(Application.AI.Common.Interfaces.Sandbox.ISandboxExecutor sandbox, string blockingSeverity = "High")
        => new(
            IacTestConfig.ValidMonitor(blockingSeverity),
            sandbox,
            NullLogger<BicepGenerator>.Instance,
            TimeProvider.System);

    private static IacGenerationRequest Request() => new()
    {
        Backend = IacBackend.Bicep,
        ResourceType = "Microsoft.Storage/storageAccounts",
        ResourceName = "primary",
        Environment = "prod",
        Parameters = new Dictionary<string, string> { ["sku"] = "Standard_LRS" }
    };

    // ---------- GenerateAsync ----------

    [Fact]
    public async Task GenerateAsync_ProducesMainBicep()
    {
        var sut = Create(new RecordingIacSandbox());

        var result = await sut.GenerateAsync(Request(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Backend.Should().Be(IacBackend.Bicep);
        result.Value.Files.Should().ContainKey("main.bicep");
        result.Value.Files["main.bicep"].Should().Contain("Microsoft.Storage/storageAccounts");
        result.Value.Files["main.bicep"].Should().Contain("sku: 'Standard_LRS'");
    }

    [Fact]
    public async Task GenerateAsync_BlankResourceName_Fails()
    {
        var sut = Create(new RecordingIacSandbox());
        var request = Request() with { ResourceName = "" };

        var result = await sut.GenerateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("iac.generate.invalid_request");
    }

    // ---------- PlanAsync ----------

    [Fact]
    public async Task PlanAsync_BuildsBicepBuildCommand()
    {
        var sandbox = new RecordingIacSandbox()
            .ForProgram("bicep", success: true, exitCode: 0, output: "{ }");
        var sut = Create(sandbox);

        await sut.PlanAsync("infra", CancellationToken.None);

        sandbox.Requests.Should().ContainSingle();
        sandbox.Requests[0].Command.Should().Be("bicep");
        sandbox.Requests[0].ArgumentList.Should().Equal("build", "main.bicep", "--stdout");
    }

    [Fact]
    public async Task PlanAsync_BuildSucceeds_Succeeded()
    {
        var sandbox = new RecordingIacSandbox()
            .ForProgram("bicep", success: true, exitCode: 0, output: "{ }");
        var sut = Create(sandbox);

        var result = await sut.PlanAsync("infra", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Succeeded.Should().BeTrue();
        result.Value.HasChanges.Should().BeFalse();
        result.Value.HasDestructiveChanges.Should().BeFalse();
    }

    [Fact]
    public async Task PlanAsync_BuildFails_NotSucceeded()
    {
        var sandbox = new RecordingIacSandbox()
            .ForProgram("bicep", success: false, exitCode: 1, output: "Error BCP028");
        var sut = Create(sandbox);

        var result = await sut.PlanAsync("infra", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task PlanAsync_SandboxThrows_ReturnsStableFailureCode()
    {
        var sut = Create(new ThrowingSandbox());

        var result = await sut.PlanAsync("infra", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("iac.plan.sandbox_error");
    }

    // ---------- ScanAsync ----------

    [Fact]
    public async Task ScanAsync_BuildsArmTtkAndCheckovCommands()
    {
        var sandbox = new RecordingIacSandbox().WithDefault(true, 0, string.Empty);
        var sut = Create(sandbox);

        await sut.ScanAsync("infra", CancellationToken.None);

        sandbox.Programs.Should().Contain("arm-ttk");
        sandbox.Programs.Should().Contain("checkov");
        sandbox.RequestFor("arm-ttk").ArgumentList.Should().Equal("-TemplatePath", ".");
        sandbox.RequestFor("checkov").ArgumentList.Should().Equal("-d", ".", "--compact", "--quiet");
    }

    [Fact]
    public async Task ScanAsync_CleanScan_Passes()
    {
        var sandbox = new RecordingIacSandbox().WithDefault(true, 0, string.Empty);
        var sut = Create(sandbox);

        var result = await sut.ScanAsync("infra", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Passed.Should().BeTrue();
        result.Value.ScannersRun.Should().Equal("arm-ttk", "checkov");
    }

    [Fact]
    public async Task ScanAsync_ParsesArmTtkFailureAsFinding()
    {
        var armttk = """
            [-] Secure-Params-No-Hardcoded-Default (12 ms)
                    Parameter 'adminPassword' must not have a hardcoded default. Severity: High
            [+] DeploymentTemplate-Schema-Is-Correct (3 ms)
            """;
        var sandbox = new RecordingIacSandbox()
            .ForProgram("arm-ttk", true, 1, armttk)
            .ForProgram("checkov", true, 0, string.Empty);
        var sut = Create(sandbox, blockingSeverity: "High");

        var result = await sut.ScanAsync("infra", CancellationToken.None);

        result.Value!.Findings.Should().ContainSingle();
        result.Value.Findings[0].Scanner.Should().Be("arm-ttk");
        result.Value.Findings[0].RuleId.Should().Be("Secure-Params-No-Hardcoded-Default");
        result.Value.Findings[0].Severity.Should().Be(IacScanSeverity.High);
        result.Value.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task ScanAsync_CombinesArmTtkAndCheckovFindings()
    {
        var armttk = "[-] Some-Rule (1 ms)\n        Detail. Severity: Medium";
        var checkov = """
            Check: CKV_AZURE_1: "Ensure foo"
                    FAILED for resource: r.x
                    Severity: LOW
            """;
        var sandbox = new RecordingIacSandbox()
            .ForProgram("arm-ttk", true, 1, armttk)
            .ForProgram("checkov", true, 1, checkov);
        var sut = Create(sandbox, blockingSeverity: "High");

        var result = await sut.ScanAsync("infra", CancellationToken.None);

        result.Value!.Findings.Should().HaveCount(2);
        // Both are below High -> passes.
        result.Value.Passed.Should().BeTrue();
    }

    private sealed class ThrowingSandbox : Application.AI.Common.Interfaces.Sandbox.ISandboxExecutor
    {
        public Task<Domain.AI.Sandbox.SandboxExecutionResult> ExecuteAsync(
            Domain.AI.Sandbox.SandboxExecutionRequest request, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
