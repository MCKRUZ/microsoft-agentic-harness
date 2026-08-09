using Domain.AI.Learnings;
using Domain.AI.Telemetry.Conventions;

namespace Application.Core.CQRS.Learnings;

/// <summary>
/// The three values every learnings write shares when it consults the memory write gate: what the
/// gate is told it is classifying, how a refusal is reported, and how the candidate is keyed
/// (issue #338).
/// </summary>
/// <remarks>
/// <para>
/// Two handlers write learning content — <see cref="RememberCommandHandler"/> creates it and
/// <see cref="ImproveLearningCommandHandler"/> replaces it — so these values exist in one place
/// rather than being restated in each. The classification ladder itself is not here; it belongs to
/// the gate. This is only the vocabulary the callers must agree on.
/// </para>
/// </remarks>
internal static class LearningsWriteGateContract
{
    /// <summary>
    /// Entity type reported to the gate. The gate's optional intent check asks "does this content
    /// match its claimed entity type", so naming the channel — not the learning's category — is what
    /// makes that question answerable.
    /// </summary>
    internal const string EntityType = "Learning";

    /// <summary>
    /// Stable, scrubbed failure code returned when the gate refuses a write. The gate's own reason
    /// string is deliberately not surfaced: it names the injection classification, and this result
    /// travels to callers that log it.
    /// </summary>
    internal const string RejectedErrorCode = "learnings.write_rejected";

    /// <summary>
    /// Builds the gate key for a candidate learning: its scope kind and category.
    /// </summary>
    /// <param name="scope">The learning's visibility scope.</param>
    /// <param name="category">What kind of knowledge the learning represents.</param>
    /// <remarks>
    /// <strong>Deliberately bounded, and that is the whole point.</strong> The gate composes this key
    /// into its audit action string, and the audit adapter uses that string verbatim as an
    /// OpenTelemetry metric tag — so a per-write unique value (a learning id, say) would mint a new
    /// time series on every write, permanently, and the background synthesis pass writes many per
    /// run. Scope-kind × category is a handful of values. It is also the honest answer to what the
    /// gate asks for — a memory key — matching the sibling knowledge channel, which supplies a
    /// stable namespaced key rather than a row id. Individual ids belong in the structured logs.
    /// <para>
    /// <strong>Known cost, accepted deliberately.</strong> Because the key is shared by every write
    /// of the same shape, the tamper-evident audit entry cannot distinguish one rejected write from
    /// another; the learning id lives only in the structured log line beside it. That is the right
    /// side of the trade — a rejected write persists nothing for the id to identify, and an
    /// ever-growing metric surface is a durable operational defect against a per-record forensic
    /// nicety. Restoring per-record audit detail means giving the gate a separate untagged
    /// correlation parameter, which is a change to a shared interface and belongs in its own issue.
    /// </para>
    /// </remarks>
    internal static string KeyFor(LearningScope scope, LearningCategory category) =>
        $"{ScopeKind(scope)}:{category}";

    private static string ScopeKind(LearningScope scope) =>
        scope.AgentId is not null ? LearningConventions.ScopeValues.Agent :
        scope.TeamId is not null ? LearningConventions.ScopeValues.Team :
        LearningConventions.ScopeValues.Global;
}
