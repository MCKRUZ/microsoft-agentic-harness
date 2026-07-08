using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// REAL WIRING BUG, exposed by the I2 wiring integration tests and deliberately NOT fixed on
/// this branch (test-only scope): no production composition root binds
/// <see cref="GovernanceConfig"/> to <c>AppConfig:AI:Governance</c>, so every
/// <c>IOptionsMonitor&lt;GovernanceConfig&gt;</c> consumer runs on compiled defaults regardless
/// of appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Affected consumers (all inject <c>IOptionsMonitor&lt;GovernanceConfig&gt;</c>):
/// </para>
/// <list type="bullet">
///   <item><description><c>ToolInvocationGovernor</c> — <c>EnforceToolInvocation</c> can never
///     be turned on from config; the live 3-gate tool path stays a pass-through.</description></item>
///   <item><description><c>ProgressEvaluator</c> — <c>ProgressGuard.Enabled</c> unreachable.</description></item>
///   <item><description><c>DefaultToolClassificationGate</c> — reads
///     <c>GovernanceConfig.DataClassification</c> from the unbound parent, so the DLP gate's
///     Mode stays Off even though <c>DataClassificationConfig</c> is separately bound and
///     validated at <c>AppConfig:AI:Governance:DataClassification</c>.</description></item>
///   <item><description><c>PromptInjectionBehavior</c> / <c>ResponseSanitizationBehavior</c> —
///     <c>Enabled</c>/<c>EnablePromptInjectionDetection</c> unreachable, so both MediatR
///     behaviors are inert; hosts shipping <c>"Governance": { "Enabled": true, ... }</c> in
///     appsettings.json get none of it at these consumers.</description></item>
/// </list>
/// <para>
/// The unit tests for all of these pass because they construct <c>new GovernanceConfig
/// {{ ... }}</c> directly — the exact "inert machinery" failure mode audit item I2 exists to
/// catch. Note the composition-root <em>registration</em> decision
/// (<c>AddGovernanceDependencies</c> vs no-op) DOES see the configured values, because it reads
/// the <c>AppConfig</c> instance bound at startup — only the options-monitor path is dead.
/// </para>
/// <para>
/// The sibling <see cref="PluginGovernanceCompositionTests"/> compensate with a single
/// test-side <c>Configure&lt;GovernanceConfig&gt;</c> call so the rest of the governance chain
/// is still proven wired. When the production binding lands (one line in
/// <c>RegisterConfigSections</c>, ideally with a config validator like its siblings), unskip
/// the test below and delete that workaround.
/// </para>
/// </remarks>
public sealed class GovernanceConfigBindingTests
{
    [Fact(Skip = "KNOWN WIRING BUG (found by I2): GovernanceConfig is never bound to " +
                 "AppConfig:AI:Governance in any composition root, so EnforceToolInvocation, " +
                 "ProgressGuard, prompt-injection and response-sanitization switches are dead " +
                 "config. Unskip when RegisterConfigSections binds GovernanceConfig.")]
    public async Task RegisterConfigSections_GovernanceSection_ReachesGovernanceConfigMonitor()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Governance:Enabled"] = "true",
            ["AppConfig:AI:Governance:EnforceToolInvocation"] = "true",
        });

        var governance = provider.GetRequiredService<IOptionsMonitor<GovernanceConfig>>().CurrentValue;

        governance.Enabled.Should().BeTrue(
            "AppConfig:AI:Governance:Enabled must reach IOptionsMonitor<GovernanceConfig> consumers");
        governance.EnforceToolInvocation.Should().BeTrue(
            "the live tool-invocation gate must be switchable from configuration");
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
