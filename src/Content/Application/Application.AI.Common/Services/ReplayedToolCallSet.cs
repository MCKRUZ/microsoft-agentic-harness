namespace Application.AI.Common.Services;

/// <summary>
/// The turn-scoped, mutable set of tool-call ids behind <see cref="ReplayedToolCallScope"/> — seeded
/// with ids replayed from earlier conversation history, then grown as
/// <c>ToolDiagnosticsMiddleware.AppendFunctionResultTracesAsync</c> records each genuinely new result.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated type rather than a bare <see cref="HashSet{T}"/> for two reasons, both about
/// concurrency. <see cref="HashSet{T}"/> is not thread-safe, and this instance is reachable from more
/// than one thread: a tool that drives an <see cref="Microsoft.Extensions.AI.IChatClient"/> through
/// the same middleware pipeline <em>without</em> going through <c>ExecuteAgentTurnCommandHandler</c>
/// inherits its parent flow's instance by reference rather than rebinding its own, and the chat client
/// is built with <c>AllowConcurrentInvocation = true</c>, so two such tools can call in at once. This
/// mirrors the locking <see cref="LlmUsageCapture"/> — the sibling turn-scoped ambient capture with
/// the identical lifetime, invoked on the very next line of that same middleware method — has always
/// applied.
/// </para>
/// <para>
/// <strong><see cref="TryClaim"/> is the whole point, and a lock alone would not have been enough.</strong>
/// The caller's question is never "is this id present?" on its own — it is "is this id present, and if
/// not, take it," so that exactly one caller records the result. Locking a separate
/// <c>Contains</c> and a separate <c>Add</c> makes each one atomic while leaving the gap between them
/// wide open: two threads both read "absent," both add, and both record — producing precisely the
/// duplicate trace record this scope exists to prevent. Claiming and testing in a single locked
/// operation is what actually closes it, so no <c>Add</c> is exposed at all.
/// </para>
/// </remarks>
public sealed class ReplayedToolCallSet
{
    private readonly HashSet<string> _callIds;
    private readonly Lock _lock = new();

    /// <summary>Initializes an empty set.</summary>
    public ReplayedToolCallSet()
        : this([])
    {
    }

    /// <summary>
    /// Initializes the set with the call ids already known before this turn dispatched.
    /// </summary>
    /// <param name="seedCallIds">
    /// The replayed history's call ids. Empty and duplicate entries are absorbed rather than rejected —
    /// this is seeded from persisted conversation rows, whose shape the seeding code does not control.
    /// </param>
    /// <remarks>
    /// Ordinal comparison, matching every other place a provider-issued call id is compared in this
    /// codebase (<c>ConversationMessageMapping</c>'s own uniqueness set is explicitly
    /// <see cref="StringComparer.Ordinal"/>). A call id is an opaque provider token, not human text:
    /// case-insensitive matching would collapse two genuinely distinct ids.
    /// </remarks>
    public ReplayedToolCallSet(IEnumerable<string> seedCallIds)
    {
        ArgumentNullException.ThrowIfNull(seedCallIds);

        _callIds = new HashSet<string>(
            seedCallIds.Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Atomically claims <paramref name="callId"/> for the caller, returning <see langword="true"/>
    /// when this caller is the first to claim it and <see langword="false"/> when it was already
    /// known — either from replayed history seeded before dispatch, or from an earlier round of this
    /// same turn.
    /// </summary>
    /// <param name="callId">The provider-issued call id. An empty id is never claimable.</param>
    /// <returns><see langword="true"/> if the caller should record this result; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// An empty id returns <see langword="false"/> rather than throwing: a result with no id cannot be
    /// deduplicated at all, so the caller has to decide what to do with it, and that decision is not
    /// this type's to make. Callers guard for the empty case before calling.
    /// </remarks>
    public bool TryClaim(string? callId) =>
        !string.IsNullOrEmpty(callId) && ClaimCore(callId);

    /// <summary>
    /// Whether <paramref name="callId"/> is already known. For assertions and diagnostics — callers on
    /// the recording path want <see cref="TryClaim"/>, whose test-and-take is a single atomic step.
    /// </summary>
    /// <param name="callId">The provider-issued call id.</param>
    public bool Contains(string? callId)
    {
        if (string.IsNullOrEmpty(callId))
            return false;

        lock (_lock)
        {
            return _callIds.Contains(callId);
        }
    }

    /// <summary>How many distinct call ids are currently known.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _callIds.Count;
            }
        }
    }

    private bool ClaimCore(string callId)
    {
        lock (_lock)
        {
            return _callIds.Add(callId);
        }
    }
}
