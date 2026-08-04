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
