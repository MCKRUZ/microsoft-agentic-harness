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
    /// File system path where conversation records are persisted by the file-backed store.
    /// </summary>
    /// <remarks>
    /// Relative paths resolve against each host's own working directory, so by default two hosts do
    /// <em>not</em> collide even though they now bind the same section. Do not give two hosts the same
    /// absolute path: the file-backed store is safe for one process at a time, and that configuration
    /// is the one that breaks it — see <c>Infrastructure.AI.Conversations.FileSystemConversationStore</c>.
    /// </remarks>
    public string ConversationsPath { get; set; } = "./conversations";
}
