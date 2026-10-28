using Domain.Common.Config.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Presentation.Common.Extensions;

namespace Presentation.Common.Compliance;

/// <summary>An operator-only compliance action: the role it needs, and the words its refusals use.</summary>
/// <param name="Role">The role the operator must hold.</param>
/// <param name="Name">The action's name in refusals ("erasure", "export").</param>
/// <param name="RoleName">The role's short name in the audit reason ("missing the erasure role").</param>
/// <param name="Verb">What the operator would do, for the tenant refusal ("may not erase").</param>
/// <param name="Detail">What the operator would do, for the response ("erase other people's data").</param>
/// <param name="BypassDetail">The response detail when sign-in is bypassed.</param>
public sealed record OperatorAction(string Role, string Name, string RoleName, string Verb, string Detail, string BypassDetail);

/// <summary>Why an operator may not take an action: a fixed phrase for the audit record, and the response to send.</summary>
/// <param name="Reason">The phrase recorded in the audit trail.</param>
/// <param name="Response">The response to return to the operator.</param>
public sealed record OperatorRefusal(string Reason, IActionResult Response);

/// <summary>
/// Decides whether the signed-in caller may take an operator-only compliance action on someone else's data: erase them
/// (#900), remove a document (#901) or export them (#902). One gate for all of them, so the rules cannot drift between
/// actions, and one for every host that mounts the compliance API, so they cannot drift between hosts either (#1003).
/// </summary>
/// <remarks>
/// <para>
/// The operator must hold the action's role, which no development identity carries, and sign in from a tenant listed in
/// <c>AppConfig:AI:Erasure:OperatorTenantIds</c>; with that list empty, operator actions are off. The role alone is not
/// enough because each organisation's administrator assigns roles in their own tenant, and these actions reach every
/// tenant. Refused while sign-in is bypassed (the host's <see cref="ComplianceApiMarker.SharedIdentitySchemes"/>), for an application's own token (an operator action must name an accountable
/// person; see <see cref="Presentation.Common.Extensions.ClaimsPrincipalExtensions.IsApplicationOnly"/>), when no usable
/// operator identity is present, and when the configuration loaded since startup is invalid.
/// </para>
/// <para>
/// The gate returns a refusal rather than answering itself, so the caller records every refusal in its audit trail before
/// responding; an authorization attribute would answer 403 before anything could be recorded.
/// </para>
/// </remarks>
public sealed class OperatorGate
{
    private readonly IOptionsMonitor<ErasureConfig> _erasureConfig;
    private readonly ComplianceApiMarker _api;

    /// <summary>Initializes a new instance of the <see cref="OperatorGate"/> class.</summary>
    /// <param name="erasureConfig">Which tenants' operators may take operator actions.</param>
    /// <param name="api">The host's opt-in, which names the sign-in schemes every caller of which is the same person.</param>
    public OperatorGate(IOptionsMonitor<ErasureConfig> erasureConfig, ComplianceApiMarker api)
    {
        ArgumentNullException.ThrowIfNull(erasureConfig);
        ArgumentNullException.ThrowIfNull(api);
        _erasureConfig = erasureConfig;
        _api = api;
    }

    /// <summary>Why the caller may not take <paramref name="action"/>, or <see langword="null"/> when they may.</summary>
    /// <param name="controller">The controller handling the request; supplies the caller and builds the response.</param>
    /// <param name="operatorId">The caller's id, or <see langword="null"/> when no usable identity is present.</param>
    /// <param name="action">The action requested.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public OperatorRefusal? Refusal(ControllerBase controller, string? operatorId, OperatorAction action)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(action);

        var user = controller.User;
        if (_api.IsSharedSyntheticIdentity(user))
            return new("sign-in is bypassed", controller.Problem(detail: action.BypassDetail, statusCode: StatusCodes.Status403Forbidden));

        // An operator action is recorded against the person who asked. An application's own token names no one: a
        // service principal granted the role could otherwise erase or export people with no human accountable.
        if (user.IsApplicationOnly())
        {
            return new("application token", controller.Problem(
                detail: $"Operator {action.Name} needs a person's sign-in: an application token names no accountable operator.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        if (operatorId is null)
            return new("no usable operator identity", controller.NoUsableIdentity());

        if (!user.IsInRole(action.Role))
            return new($"missing the {action.RoleName} role", controller.Forbid());

        List<string> allowedTenants;
        try
        {
            allowedTenants = _erasureConfig.CurrentValue.OperatorTenantIds;
        }
        catch (OptionsValidationException)
        {
            // An invalid section loaded after startup: refuse, closed, and say why in the audit trail.
            return new($"{action.Name} configuration invalid", controller.Problem(
                detail: $"Operator {action.Name} is unavailable: its configuration is invalid.",
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        if (allowedTenants.Count == 0)
        {
            return new($"operator {action.Name} is not configured", controller.Problem(
                detail: $"Operator {action.Name} is off: no tenant is allowed to run it (AppConfig:AI:Erasure:OperatorTenantIds).",
                statusCode: StatusCodes.Status403Forbidden));
        }

        if (user.GetTenantId() is not { } tenantId || !allowedTenants.Contains(tenantId, StringComparer.OrdinalIgnoreCase))
        {
            return new($"operator's tenant may not {action.Verb}", controller.Problem(
                detail: $"Operators from this tenant may not {action.Detail}.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        return null;
    }
}
