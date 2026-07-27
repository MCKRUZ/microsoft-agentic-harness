using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Presentation.Common.Escalations;

/// <summary>
/// Deliberate opt-in mount for the escalation decision API. Hosts that should serve
/// <c>/api/escalations</c> chain <see cref="AddEscalationApi"/> onto their
/// <c>AddControllers()</c> call; hosts that merely reference <c>Presentation.Common</c> for its
/// shared services never expose the routes.
/// </summary>
public static class EscalationApiMvcBuilderExtensions
{
    /// <summary>
    /// Mounts <see cref="EscalationsController"/> into the host's MVC pipeline. Two things
    /// happen: this assembly is added as an application part (required for hosts whose build
    /// did not auto-register it), and <see cref="EscalationApiMarker"/> is registered, which is
    /// what actually arms the routes — <see cref="RequiresEscalationApiOptInAttribute"/> keeps
    /// them un-matched (404) in every host without the marker, including hosts where the Web
    /// SDK auto-discovered this assembly as an application part.
    /// </summary>
    /// <param name="builder">The MVC builder returned by <c>AddControllers()</c>.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// Only mount this in a host that runs the agent workload itself: escalation state lives in
    /// the in-process <c>DefaultEscalationService</c> singleton, so the decision endpoint must
    /// be co-resident with the process whose agent turns are blocked on those escalations.
    /// </remarks>
    public static IMvcBuilder AddEscalationApi(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Idempotent: a second call must not stack duplicate hosted services or parts.
        if (builder.Services.Any(d => d.ServiceType == typeof(EscalationApiMarker)))
            return builder;

        builder.Services.TryAddSingleton<EscalationApiMarker>();

        // Advisory only in hosts that actually serve the routes: warns at startup when the
        // approver identity claim is a mutable sign-in name (UPN-reuse risk; 'oid' recommended).
        builder.Services.AddHostedService<EscalationApiMutableClaimStartupWarning>();

        return builder.AddApplicationPart(typeof(EscalationsController).Assembly);
    }
}
