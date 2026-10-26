namespace Domain.Common.Compliance;

/// <summary>
/// The person a copy of personal data is being made for (#902), and the ids that lead to their data in stores that do not
/// record an owner.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Match the owner form your writer stored.</strong> Some stores keep the id exactly as the identity provider
/// issued it (<see cref="OwnerId"/>), others the canonical lower-case form (<see cref="CanonicalOwnerId"/>). A store that
/// reads with the wrong form finds nothing and returns an empty section that looks like success.
/// </para>
/// <para>
/// <strong>Conversation ids shaped like a planned task's are left out.</strong> A conversation whose id carries the
/// reserved separator (<see cref="Helpers.ReservedConversationIds"/>) predates the rule that refuses such ids, and stores
/// that record no owner may hold another person's planned-task records under the same id, so nothing is read through it.
/// </para>
/// <para>
/// <strong>The person's planned-task runs lead to data too</strong> (<see cref="PlanRuns"/>). Their model steps ran as
/// conversations of their own and their tool steps under the run's id, so a store keyed by conversation id reads
/// <see cref="AllConversationIds"/>, and a store keyed by run reads the traces. These are the same ids erasure reaches,
/// read the same way, so a copy holds what an erasure would delete.
/// </para>
/// </remarks>
public sealed record ExportSubject
{
    /// <summary>Initializes a new instance of the <see cref="ExportSubject"/> class.</summary>
    /// <param name="ownerId">The person's id, exactly as their identity provider issues it.</param>
    /// <param name="conversationIds">The ids of the conversations the person owns.</param>
    /// <param name="planRuns">The ids the person's planned-task runs recorded data under; none when omitted.</param>
    public ExportSubject(string ownerId, IReadOnlyList<string> conversationIds, PlanRunTraces? planRuns = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(conversationIds);

        OwnerId = ownerId;
        CanonicalOwnerId = ownerId.Trim().ToLowerInvariant();
        ConversationIds = [.. conversationIds.Where(id => !Helpers.ReservedConversationIds.IsReserved(id))];
        PlanRuns = planRuns ?? PlanRunTraces.Empty;
        AllConversationIds = [.. ConversationIds.Concat(PlanRuns.StepConversationIds).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The person's id, exactly as their identity provider issues it.</summary>
    public string OwnerId { get; }

    /// <summary>The person's id in the canonical lower-case form some stores record.</summary>
    public string CanonicalOwnerId { get; }

    /// <summary>The conversations the person owns, other than any whose id is shaped like a planned task's.</summary>
    public IReadOnlyList<string> ConversationIds { get; }

    /// <summary>The ids the person's planned-task runs recorded data under.</summary>
    public PlanRunTraces PlanRuns { get; }

    /// <summary>
    /// Every conversation id the person's data is recorded under: their own conversations and their planned tasks' model
    /// steps. What a store keyed by conversation id reads; only the conversation stores use <see cref="ConversationIds"/>.
    /// </summary>
    public IReadOnlyList<string> AllConversationIds { get; }
}
