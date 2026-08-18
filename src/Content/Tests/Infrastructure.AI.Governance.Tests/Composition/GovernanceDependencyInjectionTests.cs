using Application.AI.Common.Interfaces.Governance;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Audit;
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

    // #386: Enabled now governs the declarative policy layer alone. EnablePromptInjectionDetection
    // must still stand up the real AGT-backed scanner even when Enabled is false — and because the
    // policy layer itself is off, IGovernancePolicyEngine must resolve the no-op engine, not the
    // adapter, and PolicyPaths must never be read (a configured-but-missing path here would have
    // thrown, per the test above, if it were touched).
    [Fact]
    public void AddGovernanceDependencies_DisabledWithInjectionDetectionOn_ResolvesAgtScannerAndNoOpPolicyEngine()
    {
        var config = new GovernanceConfig
        {
            Enabled = false,
            EnablePromptInjectionDetection = true,
            PolicyPaths = [Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yaml")],
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGovernanceDependencies(config);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPromptInjectionScanner>()
            .Should().BeOfType<AgtPromptInjectionAdapter>(
                "detection is independently switched on and must arm the real scanner even with the policy layer off");
        provider.GetRequiredService<IGovernancePolicyEngine>()
            .Should().BeOfType<NoOpPolicyEngine>(
                "the declarative policy layer is off (Enabled=false); a sibling feature being on must not turn it on");
    }

    // Mirrors the test above for MCP security scanning — the other independently-toggleable feature
    // area #386 decouples from Enabled.
    [Fact]
    public void AddGovernanceDependencies_DisabledWithMcpSecurityOn_ResolvesAgtScannerAndNoOpPolicyEngine()
    {
        var config = new GovernanceConfig { Enabled = false, EnableMcpSecurity = true };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGovernanceDependencies(config);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMcpSecurityScanner>()
            .Should().BeOfType<McpSecurityScannerAdapter>(
                "MCP security scanning is independently switched on and must arm the real scanner even with the policy layer off");
        provider.GetRequiredService<IGovernancePolicyEngine>()
            .Should().BeOfType<NoOpPolicyEngine>(
                "the declarative policy layer is off (Enabled=false); a sibling feature being on must not turn it on");
    }

    // The policy engine's own switch: Enabled=true must resolve the real adapter regardless of the
    // other two flags, and Enabled=false must resolve the no-op — the control for the two tests above.
    [Fact]
    public void AddGovernanceDependencies_EnabledTrue_ResolvesAgtPolicyEngineAdapter()
    {
        var config = new GovernanceConfig { Enabled = true };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGovernanceDependencies(config);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IGovernancePolicyEngine>()
            .Should().BeOfType<AgtPolicyEngineAdapter>();
    }

    // Regression guard for the bug the security review found: EnableResponseSanitization is the one
    // flag among the four ArmsAgtKernel checks that defaults to true. AddGovernance — the single entry
    // point composition roots call (#386) — must arm the kernel and resolve the REAL sanitizer chain
    // for a bare GovernanceConfig, not fall through to AddGovernanceNoOpDependencies while
    // ResponseSanitizationBehavior believes sanitization is active because its own flag reads true.
    [Fact]
    public void AddGovernance_DefaultConfig_ResolvesRealCompositeResponseSanitizer()
    {
        var config = new GovernanceConfig();
        config.ArmsAgtKernel.Should().BeTrue("EnableResponseSanitization defaults to true");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGovernance(config);

        using var provider = services.BuildServiceProvider();
        var sanitizer = provider.GetRequiredService<ICompositeResponseSanitizer>();
        sanitizer.Should().NotBeOfType<NoOpResponseSanitizer>(
            "a bare GovernanceConfig has EnableResponseSanitization=true and must not silently wire the no-op chain");
    }

    // The control: every flag off must still resolve the no-op set via AddGovernance.
    [Fact]
    public void AddGovernance_AllFlagsOff_ResolvesNoOpResponseSanitizer()
    {
        var config = new GovernanceConfig
        {
            Enabled = false,
            EnablePromptInjectionDetection = false,
            EnableMcpSecurity = false,
            EnableResponseSanitization = false,
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGovernance(config);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICompositeResponseSanitizer>()
            .Should().BeOfType<NoOpResponseSanitizer>();
    }

    // Regression guard for a security-review HIGH finding: AddGovernanceNoOpDependencies used to
    // register NoOpAuditService, so any consumer that left every kernel-arming flag off (Enabled,
    // EnablePromptInjectionDetection, EnableMcpSecurity, EnableResponseSanitization, DataClassification)
    // but still had EnableAudit=true (the default) silently lost every audit record — the call sites
    // that gate on EnableAudit (ToolInvocationGovernor, PromptInjectionBehavior, etc.) believed
    // auditing was active. The audit writer has no dependency on GovernanceKernel, so it is registered
    // for real regardless of ArmsAgtKernel.
    [Fact]
    public void AddGovernance_AllFlagsOff_StillResolvesRealAuditService()
    {
        var config = new GovernanceConfig
        {
            Enabled = false,
            EnablePromptInjectionDetection = false,
            EnableMcpSecurity = false,
            EnableResponseSanitization = false,
        };
        config.ArmsAgtKernel.Should().BeFalse("every kernel-arming flag is off");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGovernance(config);

        using var provider = services.BuildServiceProvider();
        // #407: durable JSONL writer, not the old in-memory-only AgtAuditAdapter.
        provider.GetRequiredService<IGovernanceAuditService>()
            .Should().BeOfType<JsonlGovernanceAuditWriter>(
                "EnableAudit defaults true and its call sites must not silently lose their audit trail " +
                "just because no other governance feature armed the kernel");
    }
}
