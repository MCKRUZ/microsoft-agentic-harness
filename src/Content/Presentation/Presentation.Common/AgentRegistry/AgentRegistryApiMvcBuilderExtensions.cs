using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Presentation.Common.AgentRegistry;

/// <summary>
/// Deliberate opt-in mount for the agent registry operator API. Hosts that should serve
/// <c>/api/agent-registry</c> chain <see cref="AddAgentRegistryApi"/> onto their
/// <c>AddControllers()</c> call; hosts that merely reference <c>Presentation.Common</c> for its
/// shared services never expose the route.
/// </summary>
public static class AgentRegistryApiMvcBuilderExtensions
{
    /// <summary>
    /// Mounts <see cref="AgentRegistryController"/> into the host's MVC pipeline. Two things happen:
    /// this assembly is added as an application part (required for hosts whose build did not
    /// auto-register it), and <see cref="AgentRegistryApiMarker"/> is registered, which is what
    /// actually arms the route — <see cref="RequiresAgentRegistryApiOptInAttribute"/> keeps it
    /// un-matched (404) in every host without the marker, including hosts where the Web SDK
    /// auto-discovered this assembly as an application part.
    /// </summary>
    /// <param name="builder">The MVC builder returned by <c>AddControllers()</c>.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// Only mount this in a host that owns the agent registry itself (the agent workload host): the
    /// refresh must run against the same <c>IAgentMetadataRegistry</c> singleton that host's live
    /// conversation turns resolve agents from.
    /// </remarks>
    public static IMvcBuilder AddAgentRegistryApi(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Idempotent: a second call must not stack duplicate application parts.
        if (builder.Services.Any(d => d.ServiceType == typeof(AgentRegistryApiMarker)))
            return builder;

        builder.Services.TryAddSingleton<AgentRegistryApiMarker>();

        return builder.AddApplicationPart(typeof(AgentRegistryController).Assembly);
    }
}
