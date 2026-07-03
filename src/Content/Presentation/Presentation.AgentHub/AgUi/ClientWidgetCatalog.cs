namespace Presentation.AgentHub.AgUi;

/// <summary>
/// The set of client tool names whose mid-run invocation is persisted as a re-renderable widget message,
/// so the rendered widget survives a page reload. These are the AgentHub WebUI generative-UI tools.
/// </summary>
/// <remarks>
/// The names must match the corresponding tool <c>ToolName</c> constants in <c>Infrastructure.AI.Tools</c>
/// (<c>RenderImageTool</c>/<c>RenderFormTool</c>/<c>RenderTableTool</c>); they are duplicated as literals
/// here to avoid a Presentation → Infrastructure project reference for three protocol strings — the same
/// strings the browser's widget registry already keys off. <c>render_chart</c> is intentionally excluded:
/// its client is the separate dashboard app, whose reload path does not (yet) re-render persisted widgets,
/// so persisting its calls would add empty placeholder messages there.
/// </remarks>
public sealed class ClientWidgetCatalog
{
    private static readonly HashSet<string> WidgetToolNames =
        new(["render_image", "render_form", "render_table"], StringComparer.Ordinal);

    /// <summary>
    /// Returns true when a client tool call for <paramref name="toolName"/> should be persisted as a
    /// widget message the browser can re-render on reload.
    /// </summary>
    public bool IsWidget(string toolName) => WidgetToolNames.Contains(toolName);
}
