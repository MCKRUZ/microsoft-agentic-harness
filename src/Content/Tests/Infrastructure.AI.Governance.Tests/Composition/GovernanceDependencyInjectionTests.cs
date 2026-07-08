using Application.AI.Common.Interfaces.Governance;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Governance.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Composition;

/// <summary>
/// Composition tests for <see cref="DependencyInjection.AddGovernanceDependencies"/> covering the
/// optional prompt-injection detector wiring. The Agent Governance Toolkit kernel only builds an
/// <c>InjectionDetector</c> when <see cref="GovernanceConfig.EnablePromptInjectionDetection"/> is on;
/// the registration must not attempt to register a null detector (nor the adapter that requires it)
/// when detection is off, or the composition root crashes at startup for the otherwise-valid
/// <c>Enabled=true, EnablePromptInjectionDetection=false</c> combination.
/// </summary>
public sealed class GovernanceDependencyInjectionTests
{
    [Fact]
    public void AddGovernanceDependencies_EnabledButInjectionDetectionOff_DoesNotThrowAndResolvesNoOpScanner()
    {
        // Regression guard: this exact combination used to register the kernel's null InjectionDetector
        // via AddSingleton (which throws ArgumentNullException on a null instance), crashing composition.
        var config = new GovernanceConfig { Enabled = true, EnablePromptInjectionDetection = false };

        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddGovernanceDependencies(config);

        act.Should().NotThrow(
            "governance may be enabled without prompt-injection detection; the detector is optional");

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPromptInjectionScanner>()
            .Should().BeOfType<NoOpInjectionScanner>(
                "with detection off the scanner must degrade to a no-op so every consumer still resolves");
    }

    [Fact]
    public void AddGovernanceDependencies_InjectionDetectionOn_ResolvesAgtAdapter()
    {
        var config = new GovernanceConfig { Enabled = true, EnablePromptInjectionDetection = true };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGovernanceDependencies(config);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPromptInjectionScanner>()
            .Should().BeOfType<AgtPromptInjectionAdapter>(
                "with detection on the scanner must wrap the AGT PromptInjectionDetector");
    }
}
