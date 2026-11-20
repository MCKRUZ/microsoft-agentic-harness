using System.Collections.Frozen;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// The client tool names whose mid-run invocation is persisted as a re-renderable widget message, so the
/// rendered widget survives a page reload, each with the version of its argument shape the saved widget
/// is stamped with. These are the AgentHub WebUI generative-UI tools.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue is declared once, in the WebUI's <c>src/features/chat/widgets/widgetCatalog.json</c>
/// (#342). The browser's widget registry is typed from that file, so the compiler refuses a widget missing
/// from either side. This list mirrors it rather than reading it, so this host keeps no build or runtime
/// dependency on the WebUI folder (a consumer can delete the WebUI and still build the headless host);
/// <c>ClientWidgetCatalogParityTests</c> fails if the two differ in name or version, and also fails if the
/// real composition root registers a render tool that is neither listed here nor excluded with a reason.
/// </para>
/// <para>
/// <c>render_chart</c> is intentionally excluded: its client is the separate dashboard app, whose reload
/// path does not re-render persisted widgets, so persisting its calls would add empty placeholder messages
/// there.
/// </para>
/// </remarks>
public sealed class ClientWidgetCatalog
{
    private static readonly FrozenDictionary<string, int> ShippedWidgets = new Dictionary<string, int>
    {
        ["render_image"] = 1,
        ["render_form"] = 1,
        ["render_table"] = 1,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, int> _widgets;

    /// <summary>Creates the catalogue of widgets the AgentHub WebUI renders.</summary>
    public ClientWidgetCatalog() => _widgets = ShippedWidgets;

    /// <summary>Creates a catalogue with the given widgets. Test seam for version stamping.</summary>
    internal ClientWidgetCatalog(IReadOnlyDictionary<string, int> widgets) =>
        _widgets = widgets.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Every persisted widget's tool name and current argument-shape version.</summary>
    public IReadOnlyDictionary<string, int> Widgets => _widgets;

    /// <summary>
    /// Returns true when a client tool call for <paramref name="toolName"/> should be persisted as a
    /// widget message the browser can re-render on reload, with the version to stamp it with.
    /// </summary>
    public bool TryGetVersion(string toolName, out int version) => _widgets.TryGetValue(toolName, out version);
}
