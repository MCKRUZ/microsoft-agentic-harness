using Microsoft.AspNetCore.Mvc.ActionConstraints;

namespace Presentation.Common.Compliance;

/// <summary>
/// Action constraint that removes the decorated controller's routes from route matching unless the host registered
/// <see cref="ComplianceApiMarker"/> via <see cref="ComplianceApiMvcBuilderExtensions.AddComplianceApi"/>.
/// </summary>
/// <remarks>
/// The Web SDK registers every referenced assembly that references MVC as an application part, so every web host that
/// references <c>Presentation.Common</c> discovers the compliance controllers whether or not it meant to serve them. A
/// host that mounted erasure without stating its sign-in bypass schemes could erase everyone under a bypass, so the routes
/// exist only where the host opted in. An action constraint runs during endpoint selection, before authentication, so a
/// host that did not opt in answers with a plain 404 rather than a 401 that reveals the route. Mirrors
/// <c>RequiresDriftApiOptInAttribute</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequiresComplianceApiOptInAttribute : Attribute, IActionConstraint
{
    /// <inheritdoc />
    public int Order => 0;

    /// <summary>Accepts the candidate action only when the host opted into the compliance API.</summary>
    /// <param name="context">The constraint context supplied by routing.</param>
    /// <returns><see langword="true"/> when <see cref="ComplianceApiMarker"/> is registered.</returns>
    public bool Accept(ActionConstraintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RouteContext.HttpContext.RequestServices.GetService(typeof(ComplianceApiMarker)) is not null;
    }
}
