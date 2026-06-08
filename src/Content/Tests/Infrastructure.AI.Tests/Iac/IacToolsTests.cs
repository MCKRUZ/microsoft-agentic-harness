using Application.AI.Common.Interfaces.Iac;
using Domain.AI.Iac;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.Tools.Iac;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Iac;

/// <summary>
/// Tests the three agent-facing IaC tools against a mocked
/// <see cref="IIacGenerator"/> resolved by backend key. Each tool is asserted on its
/// happy path (JSON returned), its failure pass-through, and its unknown-operation
/// rejection. The default backend is resolved from <c>AppConfig.AI.Iac.EnabledBackends</c>.
/// </summary>
public sealed class IacToolsTests
{
    private static (IServiceProvider sp, Mock<IIacGenerator> gen) ProviderWith(string backendKey = "terraform")
    {
        var gen = new Mock<IIacGenerator>();
        gen.SetupGet(g => g.Backend).Returns(IacBackend.Terraform);
        var services = new ServiceCollection();
        services.AddKeyedSingleton(backendKey, gen.Object);
        return (services.BuildServiceProvider(), gen);
    }

    private static IReadOnlyDictionary<string, object?> Params(params (string, object?)[] kv)
        => kv.ToDictionary(p => p.Item1, p => p.Item2);

    // ---------- IacGenerateTool ----------

    [Fact]
    public async Task GenerateTool_HappyPath_ReturnsOkWithJson()
    {
        var (sp, gen) = ProviderWith();
        gen.Setup(g => g.GenerateAsync(It.IsAny<IacGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IacGenerationResult>.Success(new IacGenerationResult
            {
                Backend = IacBackend.Terraform,
                Files = new Dictionary<string, string> { ["main.tf"] = "resource {}" }
            }));
        var tool = new IacGenerateTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("generate", Params(
            ("resource_type", "azurerm_storage_account"),
            ("resource_name", "primary")));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("main.tf");
    }

    [Fact]
    public async Task GenerateTool_MissingResourceType_ReturnsFail()
    {
        var (sp, _) = ProviderWith();
        var tool = new IacGenerateTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("generate", Params(("resource_name", "primary")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("resource_type");
    }

    [Fact]
    public async Task GenerateTool_GeneratorFails_ReturnsFail()
    {
        var (sp, gen) = ProviderWith();
        gen.Setup(g => g.GenerateAsync(It.IsAny<IacGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IacGenerationResult>.Fail("iac.generate.invalid_request"));
        var tool = new IacGenerateTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("generate", Params(
            ("resource_type", "x"), ("resource_name", "y")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("iac.generate.invalid_request");
    }

    [Fact]
    public async Task GenerateTool_UnknownOperation_ReturnsFail()
    {
        var (sp, _) = ProviderWith();
        var tool = new IacGenerateTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("destroy", Params());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Unknown operation");
    }

    [Fact]
    public async Task GenerateTool_UnknownBackend_ReturnsFail()
    {
        var (sp, _) = ProviderWith();
        var tool = new IacGenerateTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("generate", Params(
            ("backend", "pulumi"), ("resource_type", "x"), ("resource_name", "y")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("iac.backend_not_registered");
    }

    [Fact]
    public void GenerateTool_IsReadOnly()
    {
        var (sp, _) = ProviderWith();
        var tool = new IacGenerateTool(sp, IacTestConfig.ValidMonitor());

        tool.Name.Should().Be("iac_generate");
        tool.IsReadOnly.Should().BeTrue();
    }

    // ---------- IacPlanTool ----------

    [Fact]
    public async Task PlanTool_HappyPath_ReturnsOkWithJson()
    {
        var (sp, gen) = ProviderWith();
        gen.Setup(g => g.PlanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IacPlanResult>.Success(new IacPlanResult
            {
                Backend = IacBackend.Terraform,
                ModulePath = "modules/net",
                Succeeded = true,
                Summary = "1 to add"
            }));
        var tool = new IacPlanTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("plan", Params(("module_directory", "modules/net")));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("1 to add");
    }

    [Fact]
    public async Task PlanTool_MissingDirectory_ReturnsFail()
    {
        var (sp, _) = ProviderWith();
        var tool = new IacPlanTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("plan", Params());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("module_directory");
    }

    [Fact]
    public async Task PlanTool_GeneratorFails_ReturnsFail()
    {
        var (sp, gen) = ProviderWith();
        gen.Setup(g => g.PlanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IacPlanResult>.Fail("iac.plan.sandbox_error"));
        var tool = new IacPlanTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("plan", Params(("module_directory", "modules/net")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("iac.plan.sandbox_error");
    }

    [Fact]
    public async Task PlanTool_UnknownOperation_ReturnsFail()
    {
        var (sp, _) = ProviderWith();
        var tool = new IacPlanTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("apply", Params());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Unknown operation");
    }

    [Fact]
    public void PlanTool_IsReadOnlyAndConcurrencySafe()
    {
        var (sp, _) = ProviderWith();
        var tool = new IacPlanTool(sp, IacTestConfig.ValidMonitor());

        tool.Name.Should().Be("iac_plan");
        tool.IsReadOnly.Should().BeTrue();
        tool.IsConcurrencySafe.Should().BeTrue();
    }

    // ---------- IacScanTool ----------

    [Fact]
    public async Task ScanTool_HappyPath_ReturnsOkWithJson()
    {
        var (sp, gen) = ProviderWith();
        gen.Setup(g => g.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IacScanResult>.Success(new IacScanResult
            {
                Backend = IacBackend.Terraform,
                ModulePath = "modules/net",
                Passed = true,
                ScannersRun = ["checkov", "tfsec"]
            }));
        var tool = new IacScanTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("scan", Params(("module_directory", "modules/net")));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("checkov");
    }

    [Fact]
    public async Task ScanTool_MissingDirectory_ReturnsFail()
    {
        var (sp, _) = ProviderWith();
        var tool = new IacScanTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("scan", Params());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("module_directory");
    }

    [Fact]
    public async Task ScanTool_GeneratorFails_ReturnsFail()
    {
        var (sp, gen) = ProviderWith();
        gen.Setup(g => g.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IacScanResult>.Fail("iac.scan.sandbox_error"));
        var tool = new IacScanTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("scan", Params(("module_directory", "modules/net")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("iac.scan.sandbox_error");
    }

    [Fact]
    public async Task ScanTool_UnknownOperation_ReturnsFail()
    {
        var (sp, _) = ProviderWith();
        var tool = new IacScanTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("fix", Params());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Unknown operation");
    }

    [Fact]
    public async Task ScanTool_BackendParameterSelectsBicep()
    {
        var bicep = new Mock<IIacGenerator>();
        bicep.SetupGet(g => g.Backend).Returns(IacBackend.Bicep);
        bicep.Setup(g => g.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IacScanResult>.Success(new IacScanResult
            {
                Backend = IacBackend.Bicep,
                ModulePath = "infra",
                Passed = true,
                ScannersRun = ["arm-ttk", "checkov"]
            }));
        var services = new ServiceCollection();
        services.AddKeyedSingleton("bicep", bicep.Object);
        var sp = services.BuildServiceProvider();
        var tool = new IacScanTool(sp, IacTestConfig.ValidMonitor());

        var result = await tool.ExecuteAsync("scan", Params(
            ("backend", "bicep"), ("module_directory", "infra")));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("arm-ttk");
    }
}
