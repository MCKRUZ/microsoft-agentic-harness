using Application.Core.Validation;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="GovernanceConfigValidator"/>. The default section is valid (so omitted /
/// default hosts keep booting). The remaining landmine rules fire only when a posture is switched
/// on without the invocation-enforcement (or MCP security) flag its enforcement path actually
/// depends on — Enabled no longer gates EnablePromptInjectionDetection/EnableMcpSecurity (#386), so
/// there is no longer a rule tying those three together. Pattern: a valid baseline, mutate one
/// field per test.
/// </summary>
public class GovernanceConfigValidatorTests
{
    private readonly GovernanceConfigValidator _validator = new();

    [Fact]
    public async Task Validate_DefaultConfig_IsValid()
    {
        var result = await _validator.ValidateAsync(new GovernanceConfig());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_EnabledWithInjectionDetectionOff_IsValid()
    {
        // The exact combination the composition crash fix makes valid: governance on, detection off.
        var config = new GovernanceConfig { Enabled = true, EnablePromptInjectionDetection = false };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_EnabledWithAllFeaturesOn_IsValid()
    {
        // Mirrors the shape every host ships today.
        var config = new GovernanceConfig
        {
            Enabled = true,
            EnablePromptInjectionDetection = true,
            EnableMcpSecurity = true,
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    /// <summary>
    /// An out-of-range threshold is the worst failure shape available to this setting: the scan
    /// still runs and still logs, but no finding is ever at or above an undefined level, so nothing
    /// is withheld while the config reads <c>EnableMcpSecurity: true</c>. The two sibling thresholds
    /// have carried this rule since they were added; this one did not until it was caught in review.
    /// </summary>
    [Fact]
    public async Task Validate_McpToolBlockThresholdOutOfRange_HasError()
    {
        var config = new GovernanceConfig
        {
            Enabled = true,
            EnableMcpSecurity = true,
            McpToolBlockThreshold = (ThreatLevel)99
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GovernanceConfig.McpToolBlockThreshold));
    }

    [Fact]
    public async Task Validate_DisabledWithInjectionDetectionOn_IsValid()
    {
        // #386: EnablePromptInjectionDetection no longer requires Enabled=true. The composition root
        // arms the AGT kernel for this flag on its own, independent of the declarative policy layer.
        var config = new GovernanceConfig { Enabled = false, EnablePromptInjectionDetection = true };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_DisabledWithMcpSecurityOn_IsValid()
    {
        // #386: EnableMcpSecurity no longer requires Enabled=true, for the same reason as above.
        var config = new GovernanceConfig { Enabled = false, EnableMcpSecurity = true };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_DisabledWithEnforceToolInvocationOn_IsValid()
    {
        // EnforceToolInvocation is consumed on the live tool path independent of Enabled, so it is
        // intentionally not constrained by the landmine guard.
        var config = new GovernanceConfig { Enabled = false, EnforceToolInvocation = true };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validate_BlankPolicyPath_HasError(string blankPath)
    {
        var config = new GovernanceConfig { PolicyPaths = [blankPath] };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.StartsWith(nameof(GovernanceConfig.PolicyPaths)));
    }

    [Fact]
    public async Task Validate_OutOfRangeConflictStrategy_HasError()
    {
        var config = new GovernanceConfig { ConflictStrategy = (ConflictResolutionStrategy)999 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GovernanceConfig.ConflictStrategy));
    }

    [Fact]
    public async Task Validate_OutOfRangeInjectionBlockThreshold_HasError()
    {
        var config = new GovernanceConfig { InjectionBlockThreshold = (ThreatLevel)999 };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GovernanceConfig.InjectionBlockThreshold));
    }

    [Fact]
    public async Task Validate_BehaviorPostureOnWithoutInvocationEnforcement_HasError()
    {
        // The dead-control guard. The posture is applied inside the tool governor, which does not
        // engage at all while EnforceToolInvocation is off — so this combination is a security setting
        // switched on in configuration and read by nothing at runtime. Refusing to boot is the only
        // outcome that cannot be mistaken for protection.
        var config = new GovernanceConfig
        {
            EnforceToolInvocation = false,
            ToolBehaviorGating = new ToolBehaviorGatingConfig { RequireApprovalForNonReadOnlyTools = true },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GovernanceConfig.EnforceToolInvocation));
    }

    [Fact]
    public async Task Validate_BehaviorPostureOnWithInvocationEnforcement_IsValid()
    {
        // The control: the rule must reject only the inert combination, not the working one.
        var config = new GovernanceConfig
        {
            EnforceToolInvocation = true,
            ToolBehaviorGating = new ToolBehaviorGatingConfig { RequireApprovalForNonReadOnlyTools = true },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_StrictDriftModeOnWithoutMcpSecurity_HasError()
    {
        // Same dead-control shape as the tool-behaviour posture guard above: the collision/shadowing/
        // drift scan StrictDriftMode tunes only runs when EnableMcpSecurity is true, so this
        // combination configures a security setting that nothing at runtime reads.
        var config = new GovernanceConfig
        {
            EnableMcpSecurity = false,
            McpToolSurfaceScanning = new McpToolSurfaceScanningConfig { StrictDriftMode = true },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GovernanceConfig.EnableMcpSecurity));
    }

    [Fact]
    public async Task Validate_StrictDriftModeOnWithMcpSecurity_IsValid()
    {
        // The control: the rule must reject only the inert combination, not the working one.
        var config = new GovernanceConfig
        {
            Enabled = true,
            EnableMcpSecurity = true,
            McpToolSurfaceScanning = new McpToolSurfaceScanningConfig { StrictDriftMode = true },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validate_ExemptionWithNoStatedReason_HasError(string reason)
    {
        // An exemption is the one place the posture can be switched off for a named tool. Whoever reads
        // this list a year from now needs to know why each entry is there, and a blank reason is
        // indistinguishable from an entry added to silence a prompt.
        var config = new GovernanceConfig
        {
            ToolBehaviorGating = new ToolBehaviorGatingConfig
            {
                Exemptions = [new ToolBehaviorExemption { Tool = "notion_search", Reason = reason }],
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_ExemptionWithNoToolName_HasError()
    {
        var config = new GovernanceConfig
        {
            ToolBehaviorGating = new ToolBehaviorGatingConfig
            {
                Exemptions = [new ToolBehaviorExemption { Tool = "", Reason = "vendor confirmed it only reads" }],
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_FullyStatedExemption_IsValid()
    {
        var config = new GovernanceConfig
        {
            ToolBehaviorGating = new ToolBehaviorGatingConfig
            {
                Exemptions =
                [
                    new ToolBehaviorExemption
                    {
                        Tool = "notion_search",
                        Reason = "POST-based search endpoint; vendor confirmed it does not mutate",
                    },
                ],
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_DefaultConfig_LeavesCompositionPostureOff()
    {
        // A default is untested unless a test builds the config with nothing set. The acceptance
        // criteria for #332 require this explicitly.
        var config = new GovernanceConfig();

        config.ToolCompositionGating.DefaultPosture.Should().Be(CompositionPosture.Allow);
        config.ToolCompositionGating.Pairings.Should().BeEmpty();

        var result = await _validator.ValidateAsync(config);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_CompositionPairingRequireApprovalWithoutInvocationEnforcement_HasError()
    {
        // Same dead-control shape as the tool-behaviour posture guard above: composition RequireApproval
        // is applied inside the same governor, so it needs the same company.
        var config = new GovernanceConfig
        {
            EnforceToolInvocation = false,
            ToolCompositionGating = new ToolCompositionGatingConfig
            {
                Pairings =
                [
                    new ToolCompositionPairing
                    {
                        Source = ToolCompositionCapability.IngestsUntrustedInput,
                        Sink = ToolCompositionCapability.SendsOutbound,
                        Posture = CompositionPosture.RequireApproval,
                    },
                ],
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GovernanceConfig.EnforceToolInvocation));
    }

    [Fact]
    public async Task Validate_CompositionPairingRequireApprovalWithInvocationEnforcement_IsValid()
    {
        // The control: the rule must reject only the inert combination, not the working one.
        var config = new GovernanceConfig
        {
            EnforceToolInvocation = true,
            ToolCompositionGating = new ToolCompositionGatingConfig
            {
                Pairings =
                [
                    new ToolCompositionPairing
                    {
                        Source = ToolCompositionCapability.IngestsUntrustedInput,
                        Sink = ToolCompositionCapability.SendsOutbound,
                        Posture = CompositionPosture.RequireApproval,
                    },
                ],
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_CompositionPairingWithSourceAndSinkSwapped_HasError()
    {
        // A pairing names one source bit and one sink bit — a swapped entry would never match anything
        // the analyzer produces, silently doing nothing while looking configured.
        var config = new GovernanceConfig
        {
            ToolCompositionGating = new ToolCompositionGatingConfig
            {
                Pairings =
                [
                    new ToolCompositionPairing
                    {
                        Source = ToolCompositionCapability.SendsOutbound,
                        Sink = ToolCompositionCapability.IngestsUntrustedInput,
                        Posture = CompositionPosture.Warn,
                    },
                ],
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_DuplicateCompositionPairing_HasError()
    {
        var config = new GovernanceConfig
        {
            ToolCompositionGating = new ToolCompositionGatingConfig
            {
                Pairings =
                [
                    new ToolCompositionPairing
                    {
                        Source = ToolCompositionCapability.IngestsUntrustedInput,
                        Sink = ToolCompositionCapability.SendsOutbound,
                        Posture = CompositionPosture.Warn,
                    },
                    new ToolCompositionPairing
                    {
                        Source = ToolCompositionCapability.IngestsUntrustedInput,
                        Sink = ToolCompositionCapability.SendsOutbound,
                        Posture = CompositionPosture.RequireApproval,
                    },
                ],
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_ToolCapabilityOverrideClearingBitsWithNoServer_HasError()
    {
        // Mirrors ToolBehaviorExemption.Server: clearing a name-keyed tool's capabilities without
        // naming its server hands back the bypass that rule exists to prevent.
        var config = new GovernanceConfig
        {
            ToolCompositionGating = new ToolCompositionGatingConfig
            {
                ToolCapabilities =
                [
                    new ToolCapabilityOverride
                    {
                        Tool = "notion_search",
                        Capabilities = [],
                        Reason = "verified it does not ingest untrusted content",
                    },
                ],
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_ToolCapabilityOverrideClearingBitsWithServerNamed_IsValid()
    {
        var config = new GovernanceConfig
        {
            ToolCompositionGating = new ToolCompositionGatingConfig
            {
                ToolCapabilities =
                [
                    new ToolCapabilityOverride
                    {
                        Tool = "notion_search",
                        Server = "notion",
                        Capabilities = [],
                        Reason = "verified it does not ingest untrusted content",
                    },
                ],
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_ServerCapabilityOverrideWithEmptyCapabilities_HasError()
    {
        // A server override may only ADD bits — an empty list adds nothing, so the entry does nothing
        // while looking configured.
        var config = new GovernanceConfig
        {
            ToolCompositionGating = new ToolCompositionGatingConfig
            {
                ServerCapabilities =
                [
                    new ToolCapabilityServerOverride
                    {
                        Server = "web",
                        Capabilities = [],
                        Reason = "every tool on this server returns fetched page content",
                    },
                ],
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_FullyStatedServerCapabilityOverride_IsValid()
    {
        var config = new GovernanceConfig
        {
            ToolCompositionGating = new ToolCompositionGatingConfig
            {
                ServerCapabilities =
                [
                    new ToolCapabilityServerOverride
                    {
                        Server = "web",
                        Capabilities = [ToolCompositionCapability.IngestsUntrustedInput],
                        Reason = "every tool on this server returns fetched page content",
                    },
                ],
            },
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }
}
