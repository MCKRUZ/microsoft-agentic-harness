using Application.Core.Validation;
using Domain.Common.Config.AI.DirectToolInvocation;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="DirectInvocationApprovalWindowValidator"/> (#784): an approver's window must
/// fit inside the direct-invocation deadline, or approvals can never succeed on that surface.
/// </summary>
public sealed class DirectInvocationApprovalWindowValidatorTests
{
    private static ValidateOptionsResult Validate(
        DirectToolInvocationConfig direct, ToolApprovalConfig approval, EscalationConfig escalation) =>
        new DirectInvocationApprovalWindowValidator(
                Mock.Of<IOptionsMonitor<ToolApprovalConfig>>(m => m.CurrentValue == approval),
                Mock.Of<IOptionsMonitor<EscalationConfig>>(m => m.CurrentValue == escalation))
            .Validate(null, direct);

    private static DirectToolInvocationConfig Direct(int deadlineSeconds = 30) =>
        new() { Enabled = true, InvocationTimeout = TimeSpan.FromSeconds(deadlineSeconds) };

    private static ToolApprovalConfig Approval(int? timeoutSeconds = null) =>
        new() { Enabled = true, Approvers = ["alice"], TimeoutSeconds = timeoutSeconds };

    private static EscalationConfig Escalation(int defaultTimeoutSeconds = 300) =>
        new() { Enabled = true, DefaultTimeoutSeconds = defaultTimeoutSeconds };

    [Fact]
    public void Validate_BothDefaults_Fails()
    {
        // The case the ToolApprovalConfig docs warn about: 300s inherited against a 30s deadline.
        var result = Validate(Direct(), Approval(), Escalation());

        Assert.True(result.Failed);
        Assert.Contains("inherited from Escalation:DefaultTimeoutSeconds", result.FailureMessage);
        Assert.Contains("InvocationTimeout", result.FailureMessage);
    }

    [Fact]
    public void Validate_ExplicitApprovalTimeoutLongerThanDeadline_Fails()
    {
        var result = Validate(Direct(), Approval(timeoutSeconds: 120), Escalation());

        Assert.True(result.Failed);
        Assert.Contains("120s", result.FailureMessage);
        Assert.DoesNotContain("inherited", result.FailureMessage);
    }

    [Fact]
    public void Validate_ApprovalTimeoutEqualToDeadline_Fails()
    {
        // Equal is not "shorter": the deadline and the approval timeout race, and the deadline's denial
        // is the one the caller sees.
        var result = Validate(Direct(deadlineSeconds: 30), Approval(timeoutSeconds: 30), Escalation());

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_ApprovalTimeoutShorterThanDeadline_Succeeds()
    {
        var result = Validate(Direct(deadlineSeconds: 30), Approval(timeoutSeconds: 20), Escalation());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_InheritedTimeoutFitsInsideRaisedDeadline_Succeeds()
    {
        var result = Validate(Direct(deadlineSeconds: 600), Approval(), Escalation(defaultTimeoutSeconds: 300));

        Assert.True(result.Succeeded);
    }

    // Each gate below is a host that can never route a direct invocation to a human, so it must not
    // be forced to tune a window it does not use. Each is the single flag flipped from the failing case.
    [Fact]
    public void Validate_DirectInvocationNotEnabled_Succeeds()
    {
        var direct = Direct();
        direct.Enabled = false;

        Assert.True(Validate(direct, Approval(), Escalation()).Succeeded);
    }

    [Fact]
    public void Validate_ApprovalRoutingOff_Succeeds()
    {
        var approval = new ToolApprovalConfig { Enabled = false };

        Assert.True(Validate(Direct(), approval, Escalation()).Succeeded);
    }

    [Fact]
    public void Validate_EscalationOff_Succeeds()
    {
        var escalation = new EscalationConfig { Enabled = false, DefaultTimeoutSeconds = 300 };

        Assert.True(Validate(Direct(), Approval(), escalation).Succeeded);
    }

    [Fact]
    public void Validate_OnlyMcpEnabled_Succeeds()
    {
        // McpEnabled is on by default in every host, including hosts that never expose the endpoint,
        // so it is not an opt-in and must not trigger the rule by itself.
        var direct = new DirectToolInvocationConfig { Enabled = false, McpEnabled = true };

        Assert.True(Validate(direct, Approval(), Escalation()).Succeeded);
    }
}
