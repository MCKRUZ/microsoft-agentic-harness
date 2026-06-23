using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Application.AI.Common.Extensions;

/// <summary>
/// Registers the skill-training subsystem's pure components into DI. Composition root
/// (Presentation) calls this from <c>AddApplicationAIDependencies</c>; CQRS handlers and
/// FluentValidation validators are picked up automatically by the existing assembly scans.
/// </summary>
/// <remarks>
/// <para>
/// Default-registered services:
/// <list type="bullet">
/// <item><see cref="PatchApplier"/> — concrete pure service for applying patches.</item>
/// <item><see cref="IGateEvaluator"/> → <see cref="GateEvaluator"/>.</item>
/// <item><see cref="IPatchAggregator"/> → <see cref="PatchAggregator"/>.</item>
/// <item><see cref="IEditSelector"/> → <see cref="TopKEditSelector"/>.</item>
/// <item><see cref="EditableSurfaceRegistry"/> — code-owned editable-surface fence allowlist.</item>
/// <item><see cref="HarnessPatchValidator"/> — concrete fence (no interface seam, by design).</item>
/// <item><see cref="ISkillTrainingCheckpointStore"/> → <see cref="InMemorySkillTrainingCheckpointStore"/>.</item>
/// <item><see cref="IPatchProposer"/> → <see cref="NotConfiguredPatchProposer"/> (fail-fast default).</item>
/// <item><see cref="IRolloutRunner"/> → <see cref="NotConfiguredRolloutRunner"/> (fail-fast default).</item>
/// </list>
/// </para>
/// <para>
/// Template consumers replace the two <c>NotConfigured</c> defaults with agent-backed
/// implementations (typically in Infrastructure.AI). The default registrations use
/// <c>TryAddSingleton</c> so replacements added before this call are preserved.
/// </para>
/// </remarks>
public static class SkillTrainingDependencyInjection
{
    /// <summary>Registers the skill-training subsystem's services.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="editableSurfaces">
    /// Optional set of harness surfaces to <em>unlock</em> for the self-optimization loop, beyond the
    /// always-editable <see cref="HarnessSurface.SkillDocument"/>. Defaults to <see langword="null"/> —
    /// locked, skill-document-only. Passing surfaces here is the <em>deliberate, human-owned, code-level</em>
    /// opt-in to Self-Harness Phase 2: it is the only way to widen the fence, the loop can never do it
    /// itself, and the validating <see cref="EditableSurfaceRegistry"/> constructor throws if any
    /// requested surface is frozen by construction (denied tools, autonomy tier, content safety, the
    /// registry itself).
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSkillTrainingDependencies(
        this IServiceCollection services,
        IReadOnlyCollection<HarnessSurface>? editableSurfaces = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<PatchApplier>();
        services.TryAddSingleton<IGateEvaluator, GateEvaluator>();
        services.TryAddSingleton<IPatchAggregator, PatchAggregator>();
        services.TryAddSingleton<IEditSelector, TopKEditSelector>();

        // Widen the fence only when a consumer explicitly opts in (code, not config). SkillDocument is
        // always editable; the validating constructor throws on any frozen-by-construction surface.
        // TryAddSingleton means the default-locked registration below is a no-op once this one lands.
        if (editableSurfaces is { Count: > 0 })
        {
            var unlocked = new HashSet<HarnessSurface>(editableSurfaces) { HarnessSurface.SkillDocument };
            services.TryAddSingleton(new EditableSurfaceRegistry(unlocked));
        }

        // Default-locked fallback. Registered via an explicit factory, NOT TryAddSingleton<T>(): the
        // container's greedy constructor selection would otherwise activate the IEnumerable ctor with an
        // empty sequence (unregistered IEnumerable<T> resolves to empty), producing a registry that
        // locks every surface — including SkillDocument — and silently breaking the loop's own edits.
        services.TryAddSingleton(_ => new EditableSurfaceRegistry());
        services.TryAddSingleton<HarnessPatchValidator>();
        services.TryAddSingleton<ISkillTrainingCheckpointStore, InMemorySkillTrainingCheckpointStore>();
        services.TryAddSingleton<IPatchProposer, NotConfiguredPatchProposer>();
        services.TryAddSingleton<IRolloutRunner, NotConfiguredRolloutRunner>();

        return services;
    }
}
