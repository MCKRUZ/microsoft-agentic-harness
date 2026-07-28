using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Presentation.Common.Governance;

/// <summary>
/// Deliberate opt-in mount for the autonomy governance read API. Hosts that should serve
/// <c>/api/governance/autonomy</c> chain <see cref="AddAutonomyApi"/> onto their
/// <c>AddControllers()</c> call; hosts that merely reference <c>Presentation.Common</c> for its
/// shared services never expose the routes.
/// </summary>
public static class AutonomyApiMvcBuilderExtensions
{
    /// <summary>
    /// Mounts <see cref="AutonomyController"/> into the host's MVC pipeline. Two things
    /// happen: this assembly is added as an application part (required for hosts whose build
    /// did not auto-register it), and <see cref="AutonomyApiMarker"/> is registered, which is
    /// what actually arms the routes — <see cref="RequiresAutonomyApiOptInAttribute"/> keeps
    /// them un-matched (404) in every host without the marker, including hosts where the Web
    /// SDK auto-discovered this assembly as an application part.
    /// </summary>
    /// <param name="builder">The MVC builder returned by <c>AddControllers()</c>.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// Mount this in hosts whose governance posture the API should describe — the effective
    /// tier and decision preview are computed from the <em>host's</em> configuration and
    /// profile registry, so the answers are only meaningful in a host that runs the agent
    /// workload with that configuration.
    /// </remarks>
    public static IMvcBuilder AddAutonomyApi(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Idempotent: a second call must not stack duplicate application parts.
        if (builder.Services.Any(d => d.ServiceType == typeof(AutonomyApiMarker)))
            return builder;

        builder.Services.TryAddSingleton<AutonomyApiMarker>();

        return builder.AddApplicationPart(typeof(AutonomyController).Assembly);
    }
}
