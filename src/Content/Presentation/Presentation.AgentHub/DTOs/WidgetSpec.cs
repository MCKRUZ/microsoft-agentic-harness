using System.Text.Json;

namespace Presentation.AgentHub.DTOs;

/// <summary>
/// A generative-UI widget the agent rendered inline during a turn (an image, form, or table),
/// persisted on its <see cref="ConversationMessage"/> so it re-renders when the conversation is
/// reloaded. <see cref="Type"/> is the client tool name (for example <c>render_table</c>) that keys
/// into the browser's widget registry; <see cref="Args"/> is the validated argument payload the tool
/// sent to the client, replayed verbatim at render time.
/// </summary>
public sealed record WidgetSpec(string Type, JsonElement Args);
