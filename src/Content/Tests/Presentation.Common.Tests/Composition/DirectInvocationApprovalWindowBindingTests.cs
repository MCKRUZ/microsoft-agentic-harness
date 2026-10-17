using Domain.Common.Config.AI.DirectToolInvocation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Proves <c>DirectInvocationApprovalWindowValidator</c> actually runs in a host (#784). A validator
/// that passes its own unit tests but is bound into no options pipeline enforces nothing, which is
/// the defect this codebase has shipped repeatedly (see the "control that nothing invokes" entry in
/// CLAUDE.md).
/// </summary>
public sealed class DirectInvocationApprovalWindowBindingTests
{
    private static Dictionary<string, string?> Settings(string? approvalTimeoutSeconds)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AppConfig:AI:DirectToolInvocation:Enabled"] = "true",
            ["AppConfig:AI:Governance:Escalation:Enabled"] = "true",
            ["AppConfig:AI:Governance:Escalation:PriorityLevels:Blocking:TimeoutSeconds"] = "300",
            ["AppConfig:AI:Governance:ToolApproval:Enabled"] = "true",
            ["AppConfig:AI:Governance:ToolApproval:Approvers:0"] = "alice",
        };

        if (approvalTimeoutSeconds is not null)
            settings["AppConfig:AI:Governance:ToolApproval:TimeoutSeconds"] = approvalTimeoutSeconds;

        return settings;
    }

    [Fact]
    public async Task RegisterConfigSections_ApprovalWindowLongerThanDeadline_FailsValidation()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(Settings(approvalTimeoutSeconds: null));

        var act = () => provider.GetRequiredService<IOptionsMonitor<DirectToolInvocationConfig>>().CurrentValue;

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("tool approval waits up to 300s");
    }

    [Fact]
    public async Task RegisterConfigSections_ApprovalWindowInsideDeadline_Binds()
    {
        await using var provider = CompositionRootTestHost.BuildProvider(Settings(approvalTimeoutSeconds: "20"));

        var direct = provider.GetRequiredService<IOptionsMonitor<DirectToolInvocationConfig>>().CurrentValue;

        direct.Enabled.Should().BeTrue();
    }
}
