using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Config;

/// <summary>
/// Tests for <see cref="GovernanceConfig.ArmsAgtKernel"/> — the single decision behind
/// <c>AddGovernanceDependencies</c> vs <c>AddGovernanceNoOpDependencies</c> (#386).
/// </summary>
/// <remarks>
/// Regression guard for two bugs found during this PR's own security review, both the same
/// fail-open shape: the first cut of <c>ArmsAgtKernel</c> omitted
/// <see cref="GovernanceConfig.EnableResponseSanitization"/> (the one flag among the original four
/// that defaults to <see langword="true"/>), then a second pass found it also omitted
/// <see cref="GovernanceConfig.DataClassification"/>'s mode. Either gap meant a bare
/// <see cref="GovernanceConfig"/> — or one with only classification switched on — computed
/// <c>ArmsAgtKernel = false</c> while the corresponding consumer (<c>ResponseSanitizationBehavior</c>,
/// Purview DLP enforcement) believed its own feature was active. See
/// <see cref="GovernanceConfig.ArmsAgtKernel"/>'s remarks.
/// </remarks>
public sealed class GovernanceConfigArmsAgtKernelTests
{
    [Fact]
    public void DefaultConfig_ArmsAgtKernel_IsTrue()
    {
        // The regression itself: a bare GovernanceConfig must arm the kernel, because
        // EnableResponseSanitization defaults true and ResponseSanitizationBehavior relies on the
        // real sanitizer chain being wired whenever its own flag says sanitization is on.
        new GovernanceConfig().ArmsAgtKernel.Should().BeTrue();
    }

    [Fact]
    public void AllFiveFlagsOff_ArmsAgtKernel_IsFalse()
    {
        var config = new GovernanceConfig
        {
            Enabled = false,
            EnablePromptInjectionDetection = false,
            EnableMcpSecurity = false,
            EnableResponseSanitization = false,
            DataClassification = new DataClassificationConfig { Mode = ClassificationEnforcementMode.Off },
        };

        config.ArmsAgtKernel.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void AnySingleFlagOn_ArmsAgtKernel_IsTrue(
        bool enabled, bool injectionDetection, bool mcpSecurity, bool responseSanitization, bool dataClassificationOn)
    {
        var config = new GovernanceConfig
        {
            Enabled = enabled,
            EnablePromptInjectionDetection = injectionDetection,
            EnableMcpSecurity = mcpSecurity,
            EnableResponseSanitization = responseSanitization,
            DataClassification = new DataClassificationConfig
            {
                Mode = dataClassificationOn
                    ? ClassificationEnforcementMode.Enforce
                    : ClassificationEnforcementMode.Off,
            },
        };

        config.ArmsAgtKernel.Should().BeTrue();
    }
}
