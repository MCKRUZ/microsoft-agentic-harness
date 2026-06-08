using Domain.AI.Iac;
using FluentAssertions;
using Infrastructure.AI.Iac;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Iac;

/// <summary>
/// Unit tests for <see cref="TerraformGenerator"/>. Scaffold output is asserted
/// directly; plan/scan are exercised against a recording sandbox so the exact CLI
/// program + arguments are verified and canned CLI output is parsed into the
/// domain result shapes. Never invokes a real CLI.
/// </summary>
public sealed class TerraformGeneratorTests
{
    private static TerraformGenerator Create(Application.AI.Common.Interfaces.Sandbox.ISandboxExecutor sandbox, string blockingSeverity = "High")
        => new(
            IacTestConfig.ValidMonitor(blockingSeverity),
            sandbox,
            NullLogger<TerraformGenerator>.Instance,
            TimeProvider.System);

    private static IacGenerationRequest Request() => new()
    {
        Backend = IacBackend.Terraform,
        ResourceType = "azurerm_storage_account",
        ResourceName = "primary",
        Environment = "prod",
        Parameters = new Dictionary<string, string> { ["location"] = "eastus" }
    };

    // ---------- GenerateAsync ----------

    [Fact]
    public async Task GenerateAsync_ProducesMainAndVariablesTf()
    {
        var sut = Create(new RecordingIacSandbox());

        var result = await sut.GenerateAsync(Request(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Backend.Should().Be(IacBackend.Terraform);
        result.Value.Files.Should().ContainKey("main.tf");
        result.Value.Files.Should().ContainKey("variables.tf");
        result.Value.Files["main.tf"].Should().Contain("resource \"azurerm_storage_account\" \"primary\"");
        result.Value.Files["main.tf"].Should().Contain("location = \"eastus\"");
    }

    [Fact]
    public async Task GenerateAsync_BlankResourceType_Fails()
    {
        var sut = Create(new RecordingIacSandbox());
        var request = Request() with { ResourceType = "" };

        var result = await sut.GenerateAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("iac.generate.invalid_request");
    }

    // ---------- PlanAsync ----------

    [Fact]
    public async Task PlanAsync_BuildsValidateThenPlanCommands()
    {
        var sandbox = new RecordingIacSandbox()
            .ForProgram("terraform", success: true, exitCode: 0, output: "Success! No changes.");
        var sut = Create(sandbox);

        await sut.PlanAsync("modules/network", CancellationToken.None);

        sandbox.Requests.Should().HaveCount(2);
        sandbox.Requests[0].Command.Should().Be("terraform");
        sandbox.Requests[0].ArgumentList.Should().Equal("validate", "-no-color");
        sandbox.Requests[1].ArgumentList.Should().Equal("plan", "-no-color", "-detailed-exitcode");
    }

    [Fact]
    public async Task PlanAsync_ValidateFails_ReturnsFailedPlanWithoutRunningPlan()
    {
        var sandbox = new RecordingIacSandbox()
            .ForProgram("terraform", success: false, exitCode: 1, output: "Error: invalid HCL");
        var sut = Create(sandbox);

        var result = await sut.PlanAsync("modules/network", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Succeeded.Should().BeFalse();
        // Only validate ran; plan was skipped.
        sandbox.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task PlanAsync_PlanWithChanges_SetsHasChanges()
    {
        var sandbox = new RecordingIacSandbox()
            .ForProgram("terraform", success: true, exitCode: 2,
                output: "Plan: 3 to add, 1 to change, 0 to destroy.");
        var sut = Create(sandbox);

        var result = await sut.PlanAsync("modules/network", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Succeeded.Should().BeTrue();
        result.Value.HasChanges.Should().BeTrue();
        result.Value.HasDestructiveChanges.Should().BeFalse();
        result.Value.Summary.Should().Contain("to add");
    }

    [Fact]
    public async Task PlanAsync_DestructivePlan_SetsHasDestructiveChanges()
    {
        var sandbox = new RecordingIacSandbox()
            .ForProgram("terraform", success: true, exitCode: 2,
                output: "Plan: 1 to add, 0 to change, 2 to destroy.");
        var sut = Create(sandbox);

        var result = await sut.PlanAsync("modules/network", CancellationToken.None);

        result.Value!.HasDestructiveChanges.Should().BeTrue();
    }

    [Fact]
    public async Task PlanAsync_SandboxThrows_ReturnsStableFailureCode()
    {
        var sut = Create(new ThrowingSandbox());

        var result = await sut.PlanAsync("modules/network", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("iac.plan.sandbox_error");
    }

    [Fact]
    public async Task PlanAsync_BlankDirectory_Fails()
    {
        var sut = Create(new RecordingIacSandbox());

        var result = await sut.PlanAsync("  ", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("iac.plan.invalid_module_directory");
    }

    // ---------- ScanAsync ----------

    [Fact]
    public async Task ScanAsync_BuildsCheckovAndTfsecCommands()
    {
        var sandbox = new RecordingIacSandbox().WithDefault(true, 0, string.Empty);
        var sut = Create(sandbox);

        await sut.ScanAsync("modules/network", CancellationToken.None);

        sandbox.Programs.Should().Contain("checkov");
        sandbox.Programs.Should().Contain("tfsec");
        sandbox.RequestFor("checkov").ArgumentList.Should().Equal("-d", ".", "--compact", "--quiet");
        sandbox.RequestFor("tfsec").ArgumentList.Should().Equal(".", "--no-colour");
    }

    [Fact]
    public async Task ScanAsync_CleanScan_Passes()
    {
        var sandbox = new RecordingIacSandbox().WithDefault(true, 0, string.Empty);
        var sut = Create(sandbox);

        var result = await sut.ScanAsync("modules/network", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Passed.Should().BeTrue();
        result.Value.Findings.Should().BeEmpty();
        result.Value.ScannersRun.Should().Equal("checkov", "tfsec");
    }

    [Fact]
    public async Task ScanAsync_HighFindingWithHighBlocking_Fails()
    {
        var checkov = """
            Check: CKV_AZURE_33: "Ensure Storage logging is enabled for Queue service"
                    FAILED for resource: azurerm_storage_account.primary
                    Severity: HIGH
            """;
        var sandbox = new RecordingIacSandbox()
            .ForProgram("checkov", true, 1, checkov)
            .ForProgram("tfsec", true, 0, string.Empty);
        var sut = Create(sandbox, blockingSeverity: "High");

        var result = await sut.ScanAsync("modules/network", CancellationToken.None);

        result.Value!.Passed.Should().BeFalse();
        result.Value.Findings.Should().ContainSingle();
        result.Value.Findings[0].Scanner.Should().Be("checkov");
        result.Value.Findings[0].RuleId.Should().Be("CKV_AZURE_33");
        result.Value.Findings[0].Severity.Should().Be(IacScanSeverity.High);
    }

    [Fact]
    public async Task ScanAsync_HighFindingWithCriticalBlocking_PassesBecauseBelowThreshold()
    {
        var checkov = """
            Check: CKV_AZURE_33: "Ensure Storage logging is enabled"
                    FAILED for resource: azurerm_storage_account.primary
                    Severity: HIGH
            """;
        var sandbox = new RecordingIacSandbox()
            .ForProgram("checkov", true, 1, checkov)
            .ForProgram("tfsec", true, 0, string.Empty);
        var sut = Create(sandbox, blockingSeverity: "Critical");

        var result = await sut.ScanAsync("modules/network", CancellationToken.None);

        result.Value!.Passed.Should().BeTrue();
        result.Value.Findings.Should().ContainSingle();
    }

    [Fact]
    public async Task ScanAsync_ParsesTfsecFindings()
    {
        var tfsec = """
            Result #1 CRITICAL Storage account uses an insecure TLS version
              ID       azure-storage-use-secure-tls-policy
              Severity CRITICAL
              Resource azurerm_storage_account.primary
            """;
        var sandbox = new RecordingIacSandbox()
            .ForProgram("checkov", true, 0, string.Empty)
            .ForProgram("tfsec", true, 1, tfsec);
        var sut = Create(sandbox);

        var result = await sut.ScanAsync("modules/network", CancellationToken.None);

        result.Value!.Findings.Should().ContainSingle();
        result.Value.Findings[0].Scanner.Should().Be("tfsec");
        result.Value.Findings[0].RuleId.Should().Be("azure-storage-use-secure-tls-policy");
        result.Value.Findings[0].Severity.Should().Be(IacScanSeverity.Critical);
        result.Value.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task ScanAsync_InvalidBlockingSeverity_Fails()
    {
        var sut = Create(new RecordingIacSandbox(), blockingSeverity: "nonsense");

        var result = await sut.ScanAsync("modules/network", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("iac.scan.invalid_blocking_severity");
    }

    private sealed class ThrowingSandbox : Application.AI.Common.Interfaces.Sandbox.ISandboxExecutor
    {
        public Task<Domain.AI.Sandbox.SandboxExecutionResult> ExecuteAsync(
            Domain.AI.Sandbox.SandboxExecutionRequest request, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
