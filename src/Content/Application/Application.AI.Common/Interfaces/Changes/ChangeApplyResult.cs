namespace Application.AI.Common.Interfaces.Changes;

/// <summary>
/// The outcome of an <see cref="IChangeApplier"/>'s attempt to apply a proposal's
/// diff to its target. Returned to the <c>MergeGate</c>; <see cref="Success"/>
/// drives the proposal to <c>Merged</c>, <see cref="Failed"/> drives it to
/// <c>Rejected</c> with the failure reason captured.
/// </summary>
/// <remarks>
/// <para>
/// Structured rather than a boolean because the merge gate has to record the
/// applier's reason in the audit history. <see cref="ApplicationReference"/>
/// carries the applier-specific id of the resulting artifact — the commit SHA for
/// git, the GitOps repo path for Kubernetes, the Terraform run id for IaC — so
/// downstream consumers (drift detection baseline update, KG memory feedback) can
/// reference the actual real-world artifact the proposal produced.
/// </para>
/// <para>
/// Use the static factories (<see cref="Succeeded"/>, <see cref="Failed"/>) so the
/// invariants for each variant are explicit at the call site.
/// </para>
/// </remarks>
public sealed record ChangeApplyResult
{
    /// <summary>True when the applier completed successfully. Drives proposal to <c>Merged</c>.</summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Human-readable description. Required when <see cref="Success"/> is false;
    /// optional context when true (e.g. "1 commit pushed to origin/main").
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Applier-specific reference to the produced artifact — commit SHA, GitOps
    /// repo path, Terraform run id. Null on failure, populated on success.
    /// </summary>
    public string? ApplicationReference { get; init; }

    /// <summary>
    /// Optional <c>sha256:</c>-prefixed hash of bulk evidence (full git output,
    /// terraform plan, etc.) stored by the orchestrator in its evidence store.
    /// </summary>
    public string? EvidenceHash { get; init; }

    /// <summary>
    /// Construct a successful apply result. <paramref name="applicationReference"/>
    /// is required (no successful apply without a produced artifact).
    /// </summary>
    /// <param name="applicationReference">The applier-specific id of the resulting artifact.</param>
    /// <param name="reason">Optional human-readable context.</param>
    /// <param name="evidenceHash">Optional evidence reference.</param>
    public static ChangeApplyResult Succeeded(
        string applicationReference,
        string reason = "",
        string? evidenceHash = null)
    {
        if (string.IsNullOrWhiteSpace(applicationReference))
        {
            throw new ArgumentException(
                "Succeeded requires a non-empty application reference (commit SHA, run id, etc.).",
                nameof(applicationReference));
        }

        return new ChangeApplyResult
        {
            Success = true,
            Reason = reason,
            ApplicationReference = applicationReference,
            EvidenceHash = evidenceHash
        };
    }

    /// <summary>
    /// Construct a failed apply result. <paramref name="reason"/> must be non-empty
    /// — the merge gate puts it verbatim into the audit history.
    /// </summary>
    public static ChangeApplyResult Failed(string reason, string? evidenceHash = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "Failed requires a non-empty reason for the audit trail.",
                nameof(reason));
        }

        return new ChangeApplyResult
        {
            Success = false,
            Reason = reason,
            EvidenceHash = evidenceHash
        };
    }
}
