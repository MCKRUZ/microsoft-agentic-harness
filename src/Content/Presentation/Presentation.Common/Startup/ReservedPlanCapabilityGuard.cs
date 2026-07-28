using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Planner;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.Common.Startup;

/// <summary>
/// Composition-time guard that refuses to build a host in which a keyed <see cref="ITool"/> is
/// registered under a reserved <see cref="PlanCapabilities"/> name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The fail-open this closes.</strong> Plan-native operations (Retrieval, LlmCall) are
/// authorized by name out of the <em>same</em> <c>CapabilityEnvelope.AllowedTools</c> string space
/// as keyed tool registrations. A tool registered under <c>rag_retrieval</c> or <c>llm_call</c>
/// therefore merges two grants that a caller believes are separate: granting the plan capability
/// silently grants the tool, and granting the tool silently grants the capability. Nothing in the
/// DI container prevents the collision, and nothing at invocation time reports it — the run simply
/// succeeds with more authority than the envelope's author intended. That is a fail-open default,
/// so it is refused at composition rather than detected in review.
/// </para>
/// <para>
/// <strong>Scope — keyed <see cref="ITool"/> descriptors present in the container, and nothing
/// else.</strong> This guard sees only registrations that exist when it runs, which means only
/// first-party wiring. Tools discovered at runtime from an external MCP server or a plugin manifest
/// never appear as DI descriptors at all, so a collision arriving from a third party is invisible
/// here no matter when the guard is called. Those are excluded separately, at the point
/// <c>ToolChainBuilder</c> assembles a chain — see the remarks on
/// <see cref="PlanCapabilities"/>. The two checks are complementary, not redundant: this one fails
/// the boot because a first-party collision is our bug; the runtime one logs and drops because a
/// third party must not be able to fail every agent turn in the host.
/// </para>
/// <para>
/// <strong>Why here and not only in a test.</strong> This is a template that consumers clone; a
/// consumer's host registers its own tools and may not carry the harness's test projects forward.
/// The check therefore ships as production wiring, on the same boot-time fail-fast footing as
/// <see cref="StartupRegistrationSmokeCheck"/> and the <c>ValidateOnBuild</c> policy: a mis-wired
/// graph fails loudly at startup instead of silently at first use.
/// </para>
/// <para>
/// <strong>Where it runs.</strong> <c>BuildGlobalSolutionServices</c> invokes it immediately after
/// <c>AddGlobalProjectDependencies</c>, the point at which every harness tool registration has run;
/// all five hosts reach it through <c>GetServices</c>. It inspects
/// <see cref="ServiceDescriptor.ServiceKey"/> rather than resolving anything, so it constructs no
/// tools and cannot itself fail a boot that would otherwise succeed. A consumer that registers
/// additional tools <em>after</em> the harness composition root has returned should call it again —
/// it is idempotent and public for exactly that reason.
/// </para>
/// <para>
/// Matching is case-insensitive (<see cref="PlanCapabilities.IsReserved"/>) because the envelope
/// matches allowlist entries case-insensitively, so a key differing only in case is a real
/// collision even though keyed DI resolution itself is ordinal.
/// </para>
/// </remarks>
public static class ReservedPlanCapabilityGuard
{
    /// <summary>
    /// Throws when any keyed <see cref="ITool"/> registration in <paramref name="services"/> uses a
    /// reserved plan-capability name.
    /// </summary>
    /// <param name="services">The service collection to inspect. Not modified.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// One or more keyed <see cref="ITool"/> registrations collide with
    /// <see cref="PlanCapabilities.ReservedNames"/>. The message names every offending key and the
    /// implementation registered under it.
    /// </exception>
    public static IServiceCollection ValidateNoReservedPlanCapabilityToolKeys(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var collisions = services
            .Where(IsReservedToolRegistration)
            .Select(descriptor => $"'{descriptor.ServiceKey}' registered as {DescribeImplementation(descriptor)}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (collisions.Count == 0)
            return services;

        throw new InvalidOperationException(
            $"{collisions.Count} keyed ITool registration(s) collide with reserved plan-capability " +
            $"name(s): {string.Join("; ", collisions)}. Plan capabilities are authorized out of the " +
            "same CapabilityEnvelope.AllowedTools string space as tool keys, so granting the " +
            "capability would also grant the tool and vice versa. Re-key the tool(s) to a name that " +
            "is not in Domain.AI.Planner.PlanCapabilities.ReservedNames.");
    }

    private static bool IsReservedToolRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(ITool)
        && descriptor.ServiceKey is string key
        && PlanCapabilities.IsReserved(key);

    /// <summary>
    /// Names the implementation behind a keyed descriptor. Factory registrations — the shape every
    /// harness tool uses — expose no type, so the key alone has to carry the diagnosis there.
    /// </summary>
    private static string DescribeImplementation(ServiceDescriptor descriptor) =>
        descriptor.KeyedImplementationType?.FullName
        ?? descriptor.KeyedImplementationInstance?.GetType().FullName
        ?? "a factory-provided implementation";
}
