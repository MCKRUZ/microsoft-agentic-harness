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
    /// Optional context object. Unlike <see cref="State"/>/<see cref="Tools"/>/
    /// <see cref="ForwardedProps"/>, this one <em>is</em> read by the server — see
    /// <see cref="AvatarRunContext.TryParse"/> — as the vehicle for a caller's per-run,
    /// non-persistent context and model override (<c>{"turnContext": "...", "deploymentOverride":
    /// "..."}</c>). Any shape the parser doesn't recognise is treated the same as absent.
    /// </summary>
    public JsonElement? Context { get; init; }

    /// <summary>
    /// Optional forwarded properties. Accepted for protocol compliance; server does not use this.
    /// </summary>
    public JsonElement? ForwardedProps { get; init; }
}

/// <summary>
/// The optional per-run payload a caller may place in <see cref="RunAgentInput.Context"/>: content
/// for this one turn only, never persisted to <c>ConversationSettings</c>. See
/// <c>CallerTurnContextProvider</c> for why <see cref="TurnContext"/> exists as a distinct channel
/// from the conversation's persistent <c>SystemPromptOverride</c>.
/// </summary>
/// <param name="TurnContext">
/// Text folded into the model's context for this turn only (e.g. mood, recently retrieved memory,
/// situational continuity) — delivered via the per-invocation <c>AIContextProvider</c> rail, never
/// baked into the cached static instructions.
/// </param>
/// <param name="DeploymentOverride">
/// Model deployment to use for this one call, taking precedence over the conversation's persisted
/// <c>ConversationSettings.DeploymentName</c>. Lets a caller route an individual turn to a different
/// model (e.g. an uncensored deployment for explicit content) without a settings round-trip, which
/// AG-UI callers have no way to make — <c>ConversationSettings</c> updates are reachable only from
/// the SignalR hub.
/// </param>
public sealed record AvatarRunContext(string? TurnContext, string? DeploymentOverride)
{
    /// <summary>
    /// Parses <paramref name="context"/> into an <see cref="AvatarRunContext"/>, defensively: any
    /// shape this doesn't recognise (absent, wrong type, malformed) yields both fields
    /// <see langword="null"/> rather than throwing — a caller not using this feature (i.e. every
    /// caller until the avatar migration) must never have its run fail because of it.
    /// </summary>
    public static AvatarRunContext TryParse(JsonElement? context)
    {
        if (context is not { ValueKind: JsonValueKind.Object } obj)
            return new AvatarRunContext(null, null);

        var turnContext = obj.TryGetProperty("turnContext", out var tc) && tc.ValueKind == JsonValueKind.String
            ? tc.GetString()
            : null;
        var deploymentOverride = obj.TryGetProperty("deploymentOverride", out var dep) && dep.ValueKind == JsonValueKind.String
            ? dep.GetString()
            : null;

        return new AvatarRunContext(turnContext, deploymentOverride);
    }
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
