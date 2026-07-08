using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Regression guard for a REAL wiring bug found (and fixed) by the I2 wiring integration
/// tests: no composition root bound <see cref="GovernanceConfig"/> to
/// <c>AppConfig:AI:Governance</c>, so every <c>IOptionsMonitor&lt;GovernanceConfig&gt;</c>
/// consumer ran on compiled defaults regardless of appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Affected consumers (all inject <c>IOptionsMonitor&lt;GovernanceConfig&gt;</c>):
/// <c>ToolInvocationGovernor</c> (<c>EnforceToolInvocation</c> could never be turned on from
/// config — the live 3-gate tool path stayed a pass-through), <c>ProgressEvaluator</c>
/// (<c>ProgressGuard.Enabled</c> unreachable), <c>DefaultToolClassificationGate</c> (reads
/// <c>GovernanceConfig.DataClassification</c> from the parent, so the DLP gate's Mode stayed
/// Off even though <c>DataClassificationConfig</c> is separately bound and validated), and
/// the <c>PromptInjectionBehavior</c> / <c>ResponseSanitizationBehavior</c> MediatR behaviors
/// (<c>Enabled</c>/<c>EnablePromptInjectionDetection</c> unreachable). Hosts shipping
/// <c>"Governance": { "Enabled": true, ... }</c> in appsettings.json got none of it at these
/// consumers, while their unit tests passed by constructing <c>new GovernanceConfig</c>
/// directly — the exact "inert machinery" failure mode audit item I2 exists to catch.
/// </para>
/// <para>
/// The fix is the validated <c>GovernanceConfig</c> binding, now living in
/// <c>RegisterValidatedConfigSections</c> with a <c>GovernanceConfigValidator</c> and
/// <c>ValidateOnStart()</c> like its siblings (the tracked follow-up is closed).
/// </para>
/// </remarks>
public sealed class GovernanceConfigBindingTests
{
    [Fact]
    public async Task RegisterConfigSections_GovernanceSection_ReachesGovernanceConfigMonitor()
    {
        // Deliberately probes with Enabled=false so composition stays off the AGT kernel path
        // (AddGovernanceDependencies), isolating the options BINDING only. EnforceToolInvocation is
        // consumed on the live tool path independent of Enabled, and InjectionBlockThreshold is a
        // non-default value — together they prove the parent GovernanceConfig binding reaches the
        // monitor. Both are valid with Enabled=false, so the GovernanceConfigValidator (which reading
        // CurrentValue now runs) passes.
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Governance:EnforceToolInvocation"] = "true",
            ["AppConfig:AI:Governance:InjectionBlockThreshold"] = "Critical",
        });

        var governance = provider.GetRequiredService<IOptionsMonitor<GovernanceConfig>>().CurrentValue;

        governance.EnforceToolInvocation.Should().BeTrue(
            "the live tool-invocation gate must be switchable from configuration");
        governance.InjectionBlockThreshold.Should().Be(ThreatLevel.Critical,
            "AppConfig:AI:Governance values must reach IOptionsMonitor<GovernanceConfig> consumers");
    }

    [Fact]
    public async Task RegisterConfigSections_EscalationSubsection_ReachesItsOwnMonitor()
    {
        // Contrast pin: the Escalation SUBSECTION of the same Governance tree IS bound (and
        // validated) by RegisterConfigSections — proving the gap above is specific to the
        // parent GovernanceConfig type, not the section path.
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Governance:Escalation:Enabled"] = "true",
            ["AppConfig:AI:Governance:Escalation:DefaultTimeoutSeconds"] = "600",
            ["AppConfig:AI:Governance:Escalation:PriorityLevels:Blocking:TimeoutSeconds"] = "300",
        });

        var escalation = provider.GetRequiredService<IOptionsMonitor<EscalationConfig>>().CurrentValue;

        escalation.Enabled.Should().BeTrue();
        escalation.DefaultTimeoutSeconds.Should().Be(600);
    }
}
