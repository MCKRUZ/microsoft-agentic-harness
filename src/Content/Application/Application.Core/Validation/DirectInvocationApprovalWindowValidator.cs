using Domain.Common.Config.AI.DirectToolInvocation;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.Options;

namespace Application.Core.Validation;

/// <summary>
/// Fails startup when a host lets a human approver take longer to answer than a direct tool
/// invocation is allowed to run (#784).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a cross-section rule.</strong> A direct invocation bounds its whole request,
/// admission chain included, by <see cref="DirectToolInvocationConfig.InvocationTimeout"/> (30s by
/// default). An approval-required call waits on a human for up to
/// <see cref="ToolApprovalConfig.TimeoutSeconds"/>, which inherits
/// <see cref="EscalationConfig.DefaultTimeoutSeconds"/> (300s) when unset. With both defaults the
/// deadline fires first, the call is reported as denied, and the caller's HTTP request is held for the
/// full 30s to get there — approvals can never succeed on that surface. Neither section can see the
/// other, so neither section's own validator can catch it.
/// </para>
/// <para>
/// <strong>Only the combination that cannot work is rejected.</strong> The check applies when an operator
/// has opted in to direct invocation (<see cref="DirectToolInvocationConfig.Enabled"/>), approval
/// routing is on, and escalation itself is on. A host that leaves any of those off never routes a
/// direct invocation to a human, so it is not forced to tune a window it does not use.
/// </para>
/// <para>
/// <strong>The MCP half is deliberately not counted.</strong>
/// <see cref="DirectToolInvocationConfig.McpEnabled"/> is on by default in every host, including hosts
/// that never expose the invocation endpoint, so treating it as an opt-in would reject agent-only hosts
/// that enable approval routing with default timeouts. The MCP path is bound by the same deadline, so a
/// host that exposes it without <see cref="DirectToolInvocationConfig.Enabled"/> is not covered by this
/// rule.
/// </para>
/// <para>
/// Attached to <see cref="DirectToolInvocationConfig"/>'s options registration, which already runs
/// <c>ValidateOnStart()</c>; the other two sections are read through <see cref="IOptionsMonitor{TOptions}"/>
/// so this validator never re-enters the validation of the section it is validating.
/// </para>
/// </remarks>
public sealed class DirectInvocationApprovalWindowValidator : IValidateOptions<DirectToolInvocationConfig>
{
    private readonly IOptionsMonitor<ToolApprovalConfig> _toolApproval;
    private readonly IOptionsMonitor<EscalationConfig> _escalation;

    /// <summary>Initializes the validator.</summary>
    /// <param name="toolApproval">The approval-routing section, which owns the approver timeout.</param>
    /// <param name="escalation">The escalation section, whose default timeout an unset approver timeout inherits.</param>
    public DirectInvocationApprovalWindowValidator(
        IOptionsMonitor<ToolApprovalConfig> toolApproval,
        IOptionsMonitor<EscalationConfig> escalation)
    {
        ArgumentNullException.ThrowIfNull(toolApproval);
        ArgumentNullException.ThrowIfNull(escalation);

        _toolApproval = toolApproval;
        _escalation = escalation;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DirectToolInvocationConfig options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var approval = _toolApproval.CurrentValue;
        var escalation = _escalation.CurrentValue;

        if (!options.Enabled || !approval.Enabled || !escalation.Enabled)
            return ValidateOptionsResult.Success;

        var approvalSeconds = approval.TimeoutSeconds ?? escalation.DefaultTimeoutSeconds;
        var approvalWindow = TimeSpan.FromSeconds(approvalSeconds);

        if (approvalWindow < options.InvocationTimeout)
            return ValidateOptionsResult.Success;

        var inherited = approval.TimeoutSeconds is null
            ? $" (inherited from Escalation:DefaultTimeoutSeconds, because ToolApproval:TimeoutSeconds is not set)"
            : string.Empty;

        return ValidateOptionsResult.Fail(
            $"Configuration validation failed for '{nameof(DirectToolInvocationConfig)}.{nameof(DirectToolInvocationConfig.InvocationTimeout)}': " +
            $"tool approval waits up to {approvalSeconds}s{inherited}, but a direct invocation is cancelled after " +
            $"{options.InvocationTimeout}. Every approval-required direct invocation would be reported as denied at " +
            "the deadline, so an approver could never answer in time. Set ToolApproval:TimeoutSeconds below " +
            "DirectToolInvocation:InvocationTimeout, or raise InvocationTimeout, or turn approval routing off.");
    }
}
