using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Presentation.Common.SkillRegistry;

/// <summary>
/// Deliberate opt-in mount for the skill registry operator API. Hosts that should serve
/// <c>/api/skill-registry</c> chain <see cref="AddSkillRegistryApi"/> onto their
/// <c>AddControllers()</c> call; hosts that merely reference <c>Presentation.Common</c> for its
/// shared services never expose the route. Mirrors <c>AgentRegistryApiMvcBuilderExtensions</c>
/// (issue #705).
/// </summary>
public static class SkillRegistryApiMvcBuilderExtensions
{
    /// <summary>
    /// Mounts <see cref="SkillRegistryController"/> into the host's MVC pipeline. Two things happen:
    /// this assembly is added as an application part (required for hosts whose build did not
    /// auto-register it), and <see cref="SkillRegistryApiMarker"/> is registered, which is what
    /// actually arms the route — <see cref="RequiresSkillRegistryApiOptInAttribute"/> keeps it
    /// un-matched (404) in every host without the marker, including hosts where the Web SDK
    /// auto-discovered this assembly as an application part.
    /// </summary>
    /// <param name="builder">The MVC builder returned by <c>AddControllers()</c>.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// Only mount this in a host that owns the skill registry itself: the refresh must run against
    /// the same <c>ISkillMetadataRegistry</c> singleton that host's live conversation turns resolve
    /// skills from.
    /// </remarks>
    public static IMvcBuilder AddSkillRegistryApi(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Idempotent: a second call must not stack duplicate application parts.
        if (builder.Services.Any(d => d.ServiceType == typeof(SkillRegistryApiMarker)))
            return builder;

        builder.Services.TryAddSingleton<SkillRegistryApiMarker>();

        return builder.AddApplicationPart(typeof(SkillRegistryController).Assembly);
    }
}
