using System.Text.Json;

namespace Application.AI.Common.Models.Conversations;

/// <summary>
/// A generative-UI widget the agent rendered inline during a turn (an image, form, or table),
/// persisted on its <see cref="ConversationMessage"/> so it re-renders when the conversation is
/// reloaded. <see cref="Type"/> is the client tool name (for example <c>render_table</c>) that keys
/// into the browser's widget registry; <see cref="Args"/> is the validated argument payload the tool
/// sent to the client, replayed verbatim at render time.
/// </summary>
/// <param name="Type">The client tool name that produced the widget.</param>
/// <param name="Args">The argument payload the client rendered.</param>
/// <param name="Version">
/// The version of <paramref name="Type"/>'s argument shape this widget was saved at, taken from the WebUI
/// widget catalogue (<c>widgetCatalog.json</c>) when it was saved (#342). The browser shows a placeholder
/// instead of the widget when this is not the version it currently renders, so a change to a widget's
/// arguments leaves older saved copies visible as "no longer supported" rather than silently blank.
/// </param>
public sealed record WidgetSpec(string Type, JsonElement Args, int Version = WidgetSpec.LegacyVersion)
{
    /// <summary>
    /// The version a widget saved before widgets carried a version reads back as. Every widget was at
    /// version 1 when versioning was introduced, so an unversioned saved widget was produced by version 1.
    /// Never change it: doing so would re-label every pre-versioning widget already on disk.
    /// </summary>
    public const int LegacyVersion = 1;
}
