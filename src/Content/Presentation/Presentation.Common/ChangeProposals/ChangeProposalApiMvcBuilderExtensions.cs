using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Presentation.Common.ChangeProposals;

/// <summary>
/// Deliberate opt-in mount for the change-proposal decision API. Hosts that should serve
/// <c>/api/change-proposals</c> chain <see cref="AddChangeProposalApi"/> onto their
/// <c>AddControllers()</c> call; hosts that merely reference <c>Presentation.Common</c> for its
/// shared services never expose the routes.
/// </summary>
public static class ChangeProposalApiMvcBuilderExtensions
{
    /// <summary>
    /// Mounts <see cref="ChangeProposalsController"/> into the host's MVC pipeline. Two things
    /// happen: this assembly is added as an application part (required for hosts whose build
    /// did not auto-register it), and <see cref="ChangeProposalApiMarker"/> is registered, which
    /// is what actually arms the routes — <see cref="RequiresChangeProposalApiOptInAttribute"/>
    /// keeps them un-matched (404) in every host without the marker, including hosts where the
    /// Web SDK auto-discovered this assembly as an application part.
    /// </summary>
    /// <param name="builder">The MVC builder returned by <c>AddControllers()</c>.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Only mount this in a host that runs the change-proposal pipeline itself: proposal state
    /// lives in the in-process <c>IChangeProposalStore</c> (the default implementation is the
    /// per-process <c>InMemoryChangeProposalStore</c> singleton), so the decision endpoints must
    /// be co-resident with the process whose orchestrator owns those proposals.
    /// </para>
    /// <para>
    /// Unlike <c>AddEscalationApi()</c>, no mutable-claim startup advisory is registered here.
    /// The escalation warning names a roster-inheritance risk (a reissued UPN inherits roster
    /// entries); change proposals have no per-proposal roster — the reviewer identity claim is
    /// audit-stamping only, never an authorization input — so that specific risk does not apply.
    /// </para>
    /// </remarks>
    public static IMvcBuilder AddChangeProposalApi(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Idempotent: a second call must not stack duplicate application parts.
        if (builder.Services.Any(d => d.ServiceType == typeof(ChangeProposalApiMarker)))
            return builder;

        builder.Services.TryAddSingleton<ChangeProposalApiMarker>();

        return builder.AddApplicationPart(typeof(ChangeProposalsController).Assembly);
    }
}
