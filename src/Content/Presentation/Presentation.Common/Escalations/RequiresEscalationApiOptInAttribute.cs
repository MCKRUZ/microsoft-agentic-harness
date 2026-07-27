using Microsoft.AspNetCore.Mvc.ActionConstraints;

namespace Presentation.Common.Escalations;

/// <summary>
/// Action constraint that removes the decorated controller's routes from route matching unless
/// the host registered <see cref="EscalationApiMarker"/> via
/// <see cref="EscalationApiMvcBuilderExtensions.AddEscalationApi"/>.
/// </summary>
/// <remarks>
/// An action constraint (rather than a filter) is deliberate: constraints run during endpoint
/// <em>selection</em>, before the authentication and authorization middleware. A non-opted host
/// therefore answers escalation paths with a plain 404 — never a 401 challenge that would reveal
/// the routes exist. Filters would run after auth and could not provide that.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequiresEscalationApiOptInAttribute : Attribute, IActionConstraint
{
    /// <inheritdoc />
    public int Order => 0;

    /// <summary>
    /// Accepts the candidate action only when the host opted into the escalation API.
    /// </summary>
    /// <param name="context">The constraint context supplied by routing.</param>
    /// <returns><see langword="true"/> when <see cref="EscalationApiMarker"/> is registered.</returns>
    public bool Accept(ActionConstraintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RouteContext.HttpContext.RequestServices
            .GetService(typeof(EscalationApiMarker)) is not null;
    }
}
