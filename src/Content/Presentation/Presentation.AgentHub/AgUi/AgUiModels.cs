using System.Text.Json;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// AG-UI protocol run request. Matches the <c>RunAgentInput</c> shape from <c>@ag-ui/core</c>.
/// The server uses <see cref="ThreadId"/> to load the persisted conversation and extracts
/// only the latest user message from <see cref="Messages"/>. Other fields are accepted
/// for wire compatibility but not used (server is authoritative for tools, state, and history).
/// </summary>
public sealed record RunAgentInput
{
    /// <summary>
    /// The conversation thread ID used to load persisted conversation history.
    /// </summary>
    public required string ThreadId { get; init; }

    /// <summary>
    /// The unique ID for this run.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Optional parent run ID for tracking run hierarchy.
    /// </summary>
    public string? ParentRunId { get; init; }

    /// <summary>
    /// The messages in this run. The server extracts only the latest user message.
    /// </summary>
    public required IReadOnlyList<AgUiMessage> Messages { get; init; }

    /// <summary>
    /// Optional state object. Accepted for protocol compliance; server does not use this.
    /// </summary>
    public JsonElement? State { get; init; }

    /// <summary>
    /// Optional tools specification. Accepted for protocol compliance; server does not use this.
    /// </summary>
    public JsonElement? Tools { get; init; }

    /// <summary>
    /// Optional context object. Accepted for protocol compliance; server does not use this.
    /// </summary>
    public JsonElement? Context { get; init; }

    /// <summary>
    /// Optional forwarded properties. Accepted for protocol compliance; server does not use this.
    /// </summary>
    public JsonElement? ForwardedProps { get; init; }
}

/// <summary>
/// A message in the AG-UI protocol. Maps to the <c>Message</c> union type
/// from <c>@ag-ui/core</c>, but only <c>Id</c>, <c>Role</c>, and <c>Content</c>
/// are used by the server.
/// </summary>
public sealed record AgUiMessage
{
    /// <summary>
    /// The unique message ID.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The message role (e.g., "user", "assistant").
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// The message content text.
    /// </summary>
    public string? Content { get; init; }
}
