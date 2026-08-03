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
    /// File system path where conversation records are persisted. Relative paths resolve against the
    /// host's working directory, so two hosts sharing conversations must both be given an absolute path.
    /// </summary>
    public string ConversationsPath { get; set; } = "./conversations";
}
