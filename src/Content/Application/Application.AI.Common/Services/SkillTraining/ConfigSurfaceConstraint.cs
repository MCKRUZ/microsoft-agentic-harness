using Domain.AI.SkillTraining;

namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// The code-owned policy declaring which harness <em>configuration</em> fields a Phase 2 Step 2
/// suggestion may target, and the bounds each must stay within — the config-surface analog of the
/// <see cref="EditableSurfaceRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Where the registry governs which prose <em>surfaces</em> the loop may <em>edit</em>, this constraint
/// governs which scalar config fields the loop may <em>suggest</em> changing — and within what range.
/// Today it admits exactly one field: <see cref="HarnessSurface.ToolErrorRetryLimit"/> →
/// <see cref="MaxAttemptsField"/>, bounded to <c>[<see cref="MinMaxAttempts"/>, <see cref="MaxMaxAttempts"/>]</c>.
/// The retry config's other fields (base delay, backoff type) are <em>frozen</em> simply by not being
/// admitted here.
/// </para>
/// <para>
/// Like the registry, this is owned by code, not configuration: the bounds and allowed-field set are
/// compile-time constants the loop cannot widen. There is intentionally no DI-time widening hook (unlike
/// the registry's opt-in constructor) — a single, fixed, minimal allowlist is the whole point of a
/// suggestion-only path, and broadening it is a deliberate code change a human reviews.
/// </para>
/// </remarks>
public sealed class ConfigSurfaceConstraint
{
    /// <summary>The only config field admitted today: the transient-failure retry-attempt count.</summary>
    public const string MaxAttemptsField = "MaxAttempts";

    /// <summary>Inclusive lower bound for a suggested <see cref="MaxAttemptsField"/> value.</summary>
    public const int MinMaxAttempts = 2;

    /// <summary>Inclusive upper bound for a suggested <see cref="MaxAttemptsField"/> value.</summary>
    public const int MaxMaxAttempts = 5;

    // Ordinal (case-sensitive): field names are exact config property names. A casing variant is
    // rejected as FieldNotAllowed — the safe direction for a governance boundary (reject, never widen).
    private static readonly IReadOnlySet<string> AllowedFields =
        new HashSet<string>(StringComparer.Ordinal) { MaxAttemptsField };

    /// <summary>The single config surface this constraint governs.</summary>
    public HarnessSurface GovernedSurface => HarnessSurface.ToolErrorRetryLimit;

    /// <summary>Returns <see langword="true"/> iff this constraint governs <paramref name="surface"/>.</summary>
    /// <param name="surface">The surface a suggestion targets.</param>
    public bool GovernsSurface(HarnessSurface surface) => surface == GovernedSurface;

    /// <summary>
    /// Returns <see langword="true"/> iff <paramref name="field"/> is one this constraint admits (an
    /// exact, case-sensitive match). Frozen fields return <see langword="false"/>.
    /// </summary>
    /// <param name="field">The config field name a suggestion targets.</param>
    public bool IsFieldAllowed(string field) => field is not null && AllowedFields.Contains(field);

    /// <summary>
    /// Returns <see langword="true"/> iff <paramref name="value"/> is within the inclusive bounds for
    /// <paramref name="field"/>. All currently-admitted fields are integer-valued.
    /// </summary>
    /// <param name="field">The admitted config field (must satisfy <see cref="IsFieldAllowed"/>).</param>
    /// <param name="value">The proposed integer value.</param>
    public bool IsWithinBounds(string field, int value) => field switch
    {
        MaxAttemptsField => value >= MinMaxAttempts && value <= MaxMaxAttempts,
        _ => false
    };

    // The dotted path, from AppConfig's own root, that MaxAttemptsField actually lives at
    // (Domain.Common.Config.AI.Resilience.RetryConfig.MaxAttempts, nested AppConfig.AI.Resilience.Retry).
    // Hand-typed here rather than derived from AllowedFields, matching that set's own comment: this
    // constraint's job is a fixed, minimal, code-owned allowlist, not a general-purpose surface→path
    // mapper. A second admitted field adds one more entry to this switch, not a new mechanism.
    private static readonly IReadOnlyDictionary<string, string> ConfigPaths =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MaxAttemptsField] = "AI.Resilience.Retry.MaxAttempts"
        };

    /// <summary>
    /// Resolves the dotted path, from <c>AppConfig</c>'s own root, that a suggestion's
    /// <paramref name="surface"/>/<paramref name="field"/> pair actually reads its live value from —
    /// e.g. <c>"AI.Resilience.Retry.MaxAttempts"</c> for <see cref="HarnessSurface.ToolErrorRetryLimit"/>/<see cref="MaxAttemptsField"/>.
    /// Used to build the <c>"config:"</c>-scheme <c>Claim.Location</c> a
    /// <see cref="Domain.AI.SkillTraining.HarnessChangeSuggestion.CurrentValue"/> claim is checked
    /// against (#319). Returns <see langword="null"/> for any surface/field this constraint does not
    /// govern — callers must check <see cref="GovernsSurface"/>/<see cref="IsFieldAllowed"/> first,
    /// exactly as <see cref="IsWithinBounds"/> already requires.
    /// </summary>
    /// <param name="surface">The suggestion's target surface.</param>
    /// <param name="field">The suggestion's target field (must satisfy <see cref="IsFieldAllowed"/>).</param>
    public string? ResolveConfigPath(HarnessSurface surface, string field) =>
        GovernsSurface(surface) && ConfigPaths.TryGetValue(field, out var path) ? path : null;
}
