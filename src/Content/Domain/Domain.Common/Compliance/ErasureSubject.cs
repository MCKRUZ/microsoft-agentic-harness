namespace Domain.Common.Compliance;

/// <summary>
/// The person a right-to-erasure request is for, in every form a store may have recorded them (#900).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two forms of the owner, because the writers disagree.</strong> Conversations and ingested document chunks
/// record the user id exactly as the identity provider issued it; plans, schedules and the knowledge graph record it
/// trimmed and lower-cased. An eraser must match the form its own writer stored, or it deletes nothing and still
/// reports success. Each eraser documents which one it reads.
/// </para>
/// <para>
/// <strong>Conversation ids are collected before anything is deleted.</strong> Several stores (observability sessions,
/// tool-result spills, budget rows, prompt-usage records, the call-once ledger) carry no owner, only the conversation
/// they belong to. They are reachable only through the subject's conversations, so the ids are read once, up front, and
/// the conversations themselves are erased last.
/// </para>
/// <para>
/// <strong>So are the ids of the subject's planned-task runs</strong> (<see cref="PlanRuns"/>, #906). Their model steps
/// ran as conversations of their own and their tool steps under the run's id; only the plan store's record of each run
/// leads to them, so the plans are erased with the conversations, after every store reached through them.
/// </para>
/// </remarks>
public sealed record ErasureSubject
{
    /// <summary>Initializes a new instance of the <see cref="ErasureSubject"/> class.</summary>
    /// <param name="ownerId">The user id as issued; never blank.</param>
    /// <param name="conversationIds">The subject's conversation ids, read before any deletion.</param>
    /// <param name="planRuns">The ids the subject's planned-task runs recorded data under; none when omitted.</param>
    /// <exception cref="ArgumentException"><paramref name="ownerId"/> is blank: a blank subject is never a wildcard.</exception>
    public ErasureSubject(string ownerId, IReadOnlyList<string> conversationIds, PlanRunTraces? planRuns = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(conversationIds);

        OwnerId = ownerId;
        CanonicalOwnerId = ownerId.Trim().ToLowerInvariant();
        ConversationIds = conversationIds;
        PlanRuns = planRuns ?? PlanRunTraces.Empty;
        WithheldConversationIds = [.. conversationIds.Where(Helpers.ReservedConversationIds.IsReserved)];
        AllConversationIds = PlanRuns.StepConversationIds.Count == 0 && WithheldConversationIds.Count == 0
            ? conversationIds
            : [.. conversationIds
                .Where(id => !Helpers.ReservedConversationIds.IsReserved(id))
                .Concat(PlanRuns.StepConversationIds)
                .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The user id exactly as the identity provider issued it.</summary>
    public string OwnerId { get; }

    /// <summary>The user id trimmed and lower-cased, the form plans and the knowledge graph store.</summary>
    public string CanonicalOwnerId { get; }

    /// <summary>
    /// The subject's own conversation ids, read before any deletion. Only the conversation stores use these alone; a
    /// store keyed by conversation id reads <see cref="AllConversationIds"/>.
    /// </summary>
    public IReadOnlyList<string> ConversationIds { get; }

    /// <summary>The ids the subject's planned-task runs recorded data under, read before any deletion.</summary>
    public PlanRunTraces PlanRuns { get; }

    /// <summary>
    /// Every conversation id the subject's data is recorded under: their own conversations and the model steps of their
    /// planned tasks. What a store keyed by conversation id erases. Leaves out <see cref="WithheldConversationIds"/>.
    /// </summary>
    public IReadOnlyList<string> AllConversationIds { get; }

    /// <summary>
    /// The subject's own conversations whose id has the shape kept for ids the harness derives from planned-task runs
    /// (<see cref="Helpers.ReservedConversationIds"/>, #907): only one created before that shape was refused can have one.
    /// </summary>
    /// <remarks>
    /// The conversation stores still erase these, matched on the owner. They are left out of
    /// <see cref="AllConversationIds"/> because the stores keyed by conversation id carry no owner: under such an id they
    /// may hold another person's planned-task records, and nothing there tells the two apart. What the subject's own
    /// conversation left under that id is kept, and the receipt says so.
    /// </remarks>
    public IReadOnlyList<string> WithheldConversationIds { get; }
}
