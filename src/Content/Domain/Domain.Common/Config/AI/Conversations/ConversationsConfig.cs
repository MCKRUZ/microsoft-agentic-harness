namespace Domain.Common.Config.AI.Conversations;

/// <summary>
/// Configuration for durable conversation transcripts.
/// Bound from <c>AppConfig:AI:Conversations</c> in appsettings.json.
/// </summary>
/// <remarks>
/// This section lives under <c>AI</c> rather than under a host's own section because the transcript
/// store is shared infrastructure: both the interactive AgentHub host and the Execution API read and
/// write the same conversations. It was previously <c>AppConfig:AgentHub:ConversationsPath</c>, which
/// only one host could reach.
/// </remarks>
public sealed class ConversationsConfig
{
    /// <summary>
    /// Which implementation backs <c>IConversationStore</c>. Defaults to
    /// <see cref="ConversationStoreProvider.Sqlite"/>.
    /// </summary>
    /// <remarks>
    /// Only the settings belonging to the selected provider are read; the other provider's settings
    /// are inert rather than invalid, so switching back and forth needs no other edit.
    /// </remarks>
    public ConversationStoreProvider Provider { get; set; } = ConversationStoreProvider.Sqlite;

    /// <summary>
    /// SQLite database file path for the <see cref="ConversationStoreProvider.Sqlite"/> provider,
    /// relative to <c>AppContext.BaseDirectory</c>. Ignored by the file-backed provider.
    /// </summary>
    /// <remarks>
    /// Two hosts <em>may</em> share this path — that is the point of the SQLite provider. A relative
    /// path resolves against each host's own output directory, so sharing one database between the
    /// AgentHub and the Execution API takes a deliberate absolute path.
    /// </remarks>
    public string DatabasePath { get; set; } = "data/conversations.db";

    /// <summary>
    /// Timings for the durable turn lease that serialises turns on one conversation across hosts.
    /// Read only by the <see cref="ConversationStoreProvider.Sqlite"/> provider.
    /// </summary>
    public ConversationTurnLeaseConfig TurnLease { get; set; } = new();

    /// <summary>
    /// Governs the sweep that reclaims budget rows whose conversation no longer exists. Read only by the
    /// <see cref="ConversationStoreProvider.Sqlite"/> provider — the file-backed provider uses the
    /// in-process budget tracker, which bounds itself by evicting.
    /// </summary>
    public ConversationBudgetRetentionConfig BudgetRetention { get; set; } = new();

    /// <summary>
    /// How many of a conversation's most recent messages are replayed to the model when a durable run
    /// continues it. Bounds prompt growth: a conversation's transcript is unbounded, the window sent to
    /// the model is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by the shared multi-turn loop, so it applies to every caller that opts into a durable
    /// conversation rather than to one host. It deliberately does <em>not</em> govern the interactive
    /// AgentHub paths: the SignalR hub and orchestrator read <c>AppConfig:AgentHub:MaxHistoryMessages</c>
    /// (20), and the AG-UI handler carries its own hardcoded 50. Three settings for one concept is one
    /// too many, and consolidating them changes interactive behaviour, so it is flagged here rather than
    /// done quietly as part of unrelated work.
    /// </para>
    /// <para>
    /// Zero or negative sends no history at all — every turn starts cold. That is a real configuration,
    /// not a disabled feature, and the store reads it that way too: <c>GetHistoryForDispatch</c> is
    /// explicit that a non-positive window returns nothing rather than everything.
    /// </para>
    /// </remarks>
    public int MaxHistoryMessages { get; set; } = 50;

    /// <summary>
    /// File system path where conversation records are persisted by the
    /// <see cref="ConversationStoreProvider.FileSystem"/> provider. Ignored by the SQLite provider.
    /// </summary>
    /// <remarks>
    /// Relative paths resolve against each host's own working directory, so by default two hosts do
    /// <em>not</em> collide even though they now bind the same section. Do not give two hosts the same
    /// absolute path: the file-backed store is safe for one process at a time, and that configuration
    /// is the one that breaks it — see <c>Infrastructure.AI.Conversations.FileSystemConversationStore</c>.
    /// </remarks>
    public string ConversationsPath { get; set; } = "./conversations";
}
