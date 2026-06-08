using System.Reflection;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.Iac;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Iac;
using Domain.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Changes.Gates;
using Infrastructure.AI.Iac;
using Infrastructure.AI.Tools.Iac;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Iac;

/// <summary>
/// Verifies <see cref="IacDependencyInjection.AddIacSkillTools"/> registers the
/// three tools and both generators by keyed name, and — crucially — that the
/// private <c>RegisterIacValidator</c> swap replaces the fail-loud
/// <c>NotConfiguredValidator</c> placeholder (registered by
/// <c>RegisterChangesServices</c>) with the real
/// <see cref="IacChangeProposalValidator"/> under the
/// <see cref="ChangeTargetKind.IacDeployment"/> key.
/// </summary>
public sealed class IacDependencyInjectionTests
{
    private static readonly MethodInfo RegisterChangesMethod =
        typeof(Infrastructure.AI.DependencyInjection)
            .GetMethod("RegisterChangesServices", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo RegisterIacValidatorMethod =
        typeof(Infrastructure.AI.DependencyInjection)
            .GetMethod("RegisterIacValidator", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void RegisterChanges(IServiceCollection services) => RegisterChangesMethod.Invoke(null, [services]);
    private static void RegisterIacValidator(IServiceCollection services) => RegisterIacValidatorMethod.Invoke(null, [services]);

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(IacTestConfig.ValidMonitor());
        services.AddSingleton(TimeProvider.System);
        // The generators resolve the keyed Process sandbox executor at construction.
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Process, Mock.Of<ISandboxExecutor>());
        return services;
    }

    [Fact]
    public void AddIacSkillTools_RegistersAllThreeToolsByKeyedName()
    {
        var services = BaseServices();

        services.AddIacSkillTools();
        var sp = services.BuildServiceProvider();

        sp.GetRequiredKeyedService<ITool>(IacGenerateTool.ToolName).Should().BeOfType<IacGenerateTool>();
        sp.GetRequiredKeyedService<ITool>(IacPlanTool.ToolName).Should().BeOfType<IacPlanTool>();
        sp.GetRequiredKeyedService<ITool>(IacScanTool.ToolName).Should().BeOfType<IacScanTool>();
    }

    [Fact]
    public void AddIacSkillTools_RegistersBothGeneratorsByBackendKey()
    {
        var services = BaseServices();

        services.AddIacSkillTools();
        var sp = services.BuildServiceProvider();

        sp.GetRequiredKeyedService<IIacGenerator>("terraform").Should().BeOfType<TerraformGenerator>();
        sp.GetRequiredKeyedService<IIacGenerator>("bicep").Should().BeOfType<BicepGenerator>();
    }

    [Fact]
    public void AddIacSkillTools_GeneratorsExposeMatchingBackend()
    {
        var services = BaseServices();

        services.AddIacSkillTools();
        var sp = services.BuildServiceProvider();

        sp.GetRequiredKeyedService<IIacGenerator>("terraform").Backend.Should().Be(IacBackend.Terraform);
        sp.GetRequiredKeyedService<IIacGenerator>("bicep").Backend.Should().Be(IacBackend.Bicep);
    }

    [Fact]
    public void AddIacSkillTools_RegistersStartupValidatorAsHostedService()
    {
        var services = BaseServices();

        services.AddIacSkillTools();
        var sp = services.BuildServiceProvider();

        sp.GetServices<IHostedService>().Should().ContainSingle(h => h is IacStartupValidator);
    }

    // ---------- The validator swap (proves the placeholder is replaced) ----------

    [Fact]
    public void RegisterChangesServices_SeedsNotConfiguredPlaceholderForIacDeployment()
    {
        var services = BaseServices();

        RegisterChanges(services);
        var sp = services.BuildServiceProvider();

        // Before the swap, the IaC validator key resolves to the fail-loud placeholder.
        sp.GetRequiredKeyedService<IChangeProposalValidator>(ChangeTargetKind.IacDeployment)
            .Should().BeOfType<NotConfiguredValidator>();
    }

    [Fact]
    public void RegisterIacValidator_ReplacesPlaceholderWithRealValidator()
    {
        var services = BaseServices();

        // Compose the root in the same order the production entry point does:
        // RegisterChangesServices seeds the placeholder, then RegisterIacValidator swaps it.
        RegisterChanges(services);
        RegisterIacValidator(services);
        var sp = services.BuildServiceProvider();

        var validators = sp.GetKeyedServices<IChangeProposalValidator>(ChangeTargetKind.IacDeployment).ToList();

        validators.Should().ContainSingle();
        validators[0].Should().BeOfType<IacChangeProposalValidator>();
        validators.Should().NotContain(v => v is NotConfiguredValidator);
    }

    [Fact]
    public void RegisterIacValidator_RealValidatorHasIacPlanScanKey()
    {
        var services = BaseServices();

        RegisterChanges(services);
        RegisterIacValidator(services);
        var sp = services.BuildServiceProvider();

        sp.GetRequiredKeyedService<IChangeProposalValidator>(ChangeTargetKind.IacDeployment)
            .Key.Should().Be("iac_plan_scan");
    }

    [Fact]
    public void RegisterIacValidator_LeavesOtherTargetKindPlaceholdersIntact()
    {
        var services = BaseServices();

        RegisterChanges(services);
        RegisterIacValidator(services);
        var sp = services.BuildServiceProvider();

        // GitRepo + KubernetesResource placeholders must not be disturbed by the IaC swap.
        sp.GetRequiredKeyedService<IChangeProposalValidator>(ChangeTargetKind.GitRepo)
            .Should().BeOfType<NotConfiguredValidator>();
        sp.GetRequiredKeyedService<IChangeProposalValidator>(ChangeTargetKind.KubernetesResource)
            .Should().BeOfType<NotConfiguredValidator>();
    }
}
