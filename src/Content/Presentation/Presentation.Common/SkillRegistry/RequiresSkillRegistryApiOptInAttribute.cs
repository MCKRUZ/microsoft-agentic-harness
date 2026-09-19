using Microsoft.AspNetCore.Mvc.ActionConstraints;

namespace Presentation.Common.SkillRegistry;

/// <summary>
/// Action constraint that removes the decorated controller's routes from route matching unless the
/// host registered <see cref="SkillRegistryApiMarker"/> via
/// <see cref="SkillRegistryApiMvcBuilderExtensions.AddSkillRegistryApi"/>.
/// </summary>
/// <remarks>
/// An action constraint (rather than a filter) is deliberate: constraints run during endpoint
/// <em>selection</em>, before the authentication and authorization middleware. A non-opted host
/// therefore answers this API's paths with a plain 404 — never a 401 challenge that would reveal the
/// routes exist. Mirrors <c>RequiresAgentRegistryApiOptInAttribute</c> (issue #705).
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequiresSkillRegistryApiOptInAttribute : Attribute, IActionConstraint
{
    /// <inheritdoc />
    public int Order => 0;

    /// <summary>Accepts the candidate action only when the host opted into the skill registry API.</summary>
    /// <param name="context">The constraint context supplied by routing.</param>
    /// <returns><see langword="true"/> when <see cref="SkillRegistryApiMarker"/> is registered.</returns>
    public bool Accept(ActionConstraintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RouteContext.HttpContext.RequestServices
            .GetService(typeof(SkillRegistryApiMarker)) is not null;
    }
}
