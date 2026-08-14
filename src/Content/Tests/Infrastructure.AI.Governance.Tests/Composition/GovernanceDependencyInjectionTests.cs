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

    // #384: GovernanceKernel's constructor loads every configured PolicyPaths entry itself
    // (PolicyEngine.LoadYamlFile in its own loop) — this is the actual path that loaded the harness's
    // own miscased default-policy.yaml, entirely bypassing AgtPolicyEngineAdapter.LoadPolicyFile. A
    // guard that only lives on the adapter's method never runs here, so this proves the guard fires on
    // the real startup path, not only on the separate runtime-load API a production host never calls.
    [Fact]
    public void AddGovernanceDependencies_ConfiguredPolicyPathUsesMisCasedDefaultAction_ThrowsBeforeKernelConstruction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, """
            name: casing-mistake
            defaultAction: allow
            rules:
              - name: block-exec
                condition: "tool == 'execute_command'"
                action: deny
            """);

        try
        {
            var config = new GovernanceConfig { Enabled = true, PolicyPaths = [path] };
            var services = new ServiceCollection();
            services.AddLogging();

            var act = () => services.AddGovernanceDependencies(config);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*defaultAction*default_action*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A configured-but-missing path used to be silently dropped via .Where(File.Exists) — the whole
    // declarative policy layer could end up unloaded with no signal beyond a quieter-than-expected
    // policy set. Fail loudly instead, before GovernanceKernel is even constructed.
    [Fact]
    public void AddGovernanceDependencies_ConfiguredPolicyPathDoesNotExist_ThrowsBeforeKernelConstruction()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yaml");
        var config = new GovernanceConfig { Enabled = true, PolicyPaths = [missingPath] };
        var services = new ServiceCollection();
        services.AddLogging();

        var act = () => services.AddGovernanceDependencies(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{missingPath}*");
    }
}
