using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Presentation.Common.Drift;

/// <summary>
/// Deliberate opt-in mount for the drift monitoring API. Hosts that should serve
/// <c>/api/drift</c> chain <see cref="AddDriftApi"/> onto their <c>AddControllers()</c> call;
/// hosts that merely reference <c>Presentation.Common</c> for its shared services never expose
/// the routes.
/// </summary>
public static class DriftApiMvcBuilderExtensions
{
    /// <summary>
    /// Mounts <see cref="DriftController"/> into the host's MVC pipeline. Two things happen:
    /// this assembly is added as an application part (required for hosts whose build did not
    /// auto-register it), and <see cref="DriftApiMarker"/> is registered, which is what
    /// actually arms the routes — <see cref="RequiresDriftApiOptInAttribute"/> keeps them
    /// un-matched (404) in every host without the marker, including hosts where the Web SDK
    /// auto-discovered this assembly as an application part.
    /// </summary>
    /// <param name="builder">The MVC builder returned by <c>AddControllers()</c>.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// Only mount this in a host that runs the drift subsystem itself (the agent workload
    /// host): pushed evaluations must land in the same stores and EWMA state the workload's
    /// own evaluations use, and drift escalations fire against that host's in-process
    /// escalation service.
    /// </remarks>
    public static IMvcBuilder AddDriftApi(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Idempotent: a second call must not stack duplicate application parts.
        if (builder.Services.Any(d => d.ServiceType == typeof(DriftApiMarker)))
            return builder;

        builder.Services.TryAddSingleton<DriftApiMarker>();

        return builder.AddApplicationPart(typeof(DriftController).Assembly);
    }
}
