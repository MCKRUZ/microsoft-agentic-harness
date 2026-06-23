using Domain.AI.SkillTraining;

namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// The editable-surface fence: hard-rejects, before the accept-gate, any skill-training patch whose
/// edits target a harness surface the <see cref="EditableSurfaceRegistry"/> has not marked editable.
/// </summary>
/// <remarks>
/// <para>
/// This type is intentionally a concrete sealed class with no interface seam. A fence a consumer could
/// replace with a permissive no-op via dependency injection would not be a fence; the only
/// configurable part of the policy is the <see cref="EditableSurfaceRegistry"/> it reads — and that
/// registry refuses, by construction, to mark governance surfaces editable.
/// </para>
/// <para>
/// <see cref="Validate"/> is pure and side-effect free, like <c>PatchApplier</c> and
/// <c>GateEvaluator</c>. Auditing a rejection and recording the rejected step are the caller's
/// responsibility (the training loop), which keeps this component deterministically testable.
/// </para>
/// </remarks>
public sealed class HarnessPatchValidator
{
    private readonly EditableSurfaceRegistry _registry;

    /// <summary>Initializes a new instance of the <see cref="HarnessPatchValidator"/> class.</summary>
    /// <param name="registry">The code-owned editable-surface registry to enforce.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
    public HarnessPatchValidator(EditableSurfaceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>
    /// Validates that every edit in <paramref name="patch"/> targets an editable surface.
    /// </summary>
    /// <param name="patch">The selected patch about to be applied.</param>
    /// <returns>
    /// <see cref="HarnessPatchValidation.Allowed"/> when every edit targets an editable surface;
    /// otherwise a result listing each edit that targeted a frozen surface.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="patch"/> is <see langword="null"/>.</exception>
    public HarnessPatchValidation Validate(Patch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        List<FrozenSurfaceViolation>? violations = null;
        for (var i = 0; i < patch.Edits.Count; i++)
        {
            var surface = patch.Edits[i].Surface;
            if (_registry.IsEditable(surface))
            {
                continue;
            }

            (violations ??= []).Add(new FrozenSurfaceViolation
            {
                EditIndex = i,
                Surface = surface,
                FrozenByConstruction = _registry.IsFrozenByConstruction(surface)
            });
        }

        return violations is null
            ? HarnessPatchValidation.Allowed
            : new HarnessPatchValidation { IsAllowed = false, Violations = violations };
    }

    /// <summary>
    /// Returns <see langword="true"/> iff the code-owned registry marks <paramref name="surface"/>
    /// editable. Exposed so the training loop can fail fast when a run declares a
    /// <c>TargetSurface</c> the registry has not unlocked, instead of silently rejecting every step.
    /// </summary>
    /// <param name="surface">The surface to test.</param>
    public bool IsSurfaceEditable(HarnessSurface surface) => _registry.IsEditable(surface);
}
