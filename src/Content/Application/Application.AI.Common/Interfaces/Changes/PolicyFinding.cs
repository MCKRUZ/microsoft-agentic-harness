namespace Application.AI.Common.Interfaces.Changes;

/// <summary>
/// One structured finding emitted by an <see cref="IChangeProposalPolicy"/>.
/// The <c>PolicyGate</c> aggregates findings from every registered policy, picks
/// the highest severity, and maps to <c>GateResult.Pass</c> or <c>Fail</c>
/// based on the configured threshold.
/// </summary>
/// <remarks>
/// <para>
/// Structured rather than a free-form string so dashboards can group findings by
/// severity, by policy key, and by location. The orchestrator stores the full
/// finding list in its content-addressed evidence store referenced by
/// <c>GateResult.EvidenceHash</c>; audit lines stay small.
/// </para>
/// <para>
/// <see cref="Location"/> identifies where in the diff or target the finding
/// applies. Free-form because the meaningful coordinate system depends on the
/// target type — a file path + line number for git, a resource path for k8s, a
/// resource id for IaC. Policies are responsible for producing a location that
/// the corresponding <c>IChangeApplier</c> would recognize.
/// </para>
/// </remarks>
public sealed record PolicyFinding
{
    /// <summary>The policy key (registered DI key) that produced this finding. Surfaces in audit and dashboards.</summary>
    public required string PolicyKey { get; init; }

    /// <summary>The finding severity. Drives the PolicyGate's pass/fail decision via the configured threshold.</summary>
    public required PolicyFindingSeverity Severity { get; init; }

    /// <summary>A short human-readable description of the finding. Surfaces in approval prompts and audit lines.</summary>
    public required string Message { get; init; }

    /// <summary>Optional structured location within the diff or target. Empty when the finding applies to the proposal as a whole.</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>
    /// Optional remediation hint — a short string describing what would make this
    /// finding go away. Surfaces in approval prompts so reviewers can suggest the
    /// fix to the originating agent.
    /// </summary>
    public string? Remediation { get; init; }

    /// <summary>
    /// When <see langword="true"/>, this finding blocks the gate regardless of
    /// <see cref="Severity"/> or the configured threshold. Defaults to <see langword="false"/>, so
    /// every existing policy is unaffected by this field's mere existence. A policy sets this only
    /// for a finding it has independently confirmed, not merely suspected — see
    /// <see cref="RequiresVerification"/> for the complementary case.
    /// </summary>
    public bool Blocking { get; init; }

    /// <summary>
    /// When <see langword="true"/>, this finding's own <see cref="Severity"/> is not trusted to
    /// block the gate on its own — the severity was assigned by a model, not confirmed against the
    /// artifact the finding is about. <c>PolicyGate</c> excludes such a finding from its severity
    /// comparison entirely (it neither blocks nor "passes below threshold"; it is simply not counted
    /// toward the gate decision) unless a verifier has independently confirmed it and the policy
    /// re-emits it with <see cref="Blocking"/> set instead. Defaults to <see langword="false"/>, so
    /// every existing policy's findings gate exactly as they always have.
    /// </summary>
    public bool RequiresVerification { get; init; }
}
