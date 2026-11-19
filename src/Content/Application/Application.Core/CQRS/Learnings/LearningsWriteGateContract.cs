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
    /// <para>
    /// <strong>What the key is used for.</strong> The gate keeps no state between writes: it scans
    /// each candidate on its own and holds no per-key rate, contradiction or history record. The key
    /// only labels the decision — it becomes part of the action text of the gate's governance audit
    /// record (or of the log line, when no audit service is registered). So two writes that share a
    /// key share nothing but that label, and a user-scoped learning
    /// (<see cref="LearningScope.UserId"/>) needs no user axis here for one user's writes to stay
    /// independent of another's (#1038).
    /// </para>
    /// <para>
    /// <strong>Why scope-kind × category and nothing finer.</strong> The audit record is a
    /// tamper-evident, append-only chain, so whatever the key carries is kept there for good. A user id
    /// would put a personal identifier into that chain on every user-scoped write. The metric that
    /// counts audit events is not a reason: since #903 it is tagged with the action's kind only
    /// (<c>memory_write</c>), never the key.
    /// </para>
    /// <para>
    /// <strong>Known cost, accepted deliberately.</strong> Because the key is shared by every write
    /// of the same shape, the audit entry cannot distinguish one rejected write from another; the
    /// learning id lives only in the structured log line beside it. A rejected write persists nothing
    /// for the id to identify, so the loss is small.
    /// </para>
    /// </remarks>
    internal static string KeyFor(LearningScope scope, LearningCategory category) =>
        $"{ScopeKind(scope)}:{category}";

    private static string ScopeKind(LearningScope scope) =>
        scope.AgentId is not null ? LearningConventions.ScopeValues.Agent :
        scope.TeamId is not null ? LearningConventions.ScopeValues.Team :
        LearningConventions.ScopeValues.Global;
}
