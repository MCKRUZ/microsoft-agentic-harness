using System.Text.Json;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Renders an image inline in the agent's chat answer ("generative UI"): the agent supplies an image
/// URL (and optional alt text / caption), and the connected browser displays it in the transcript.
/// The browser returns a short textual acknowledgement so the agent can narrate what it showed.
/// </summary>
/// <remarks>
/// <para>
/// A client round-trip tool using the same blocking-proxy mechanism as <see cref="RenderChartTool"/>:
/// it delegates the render to the connected browser via <see cref="IClientToolBridge"/> and returns the
/// browser's acknowledgement. The image itself is fetched and displayed by the client; only the URL and
/// metadata flow through the model. The URL is validated to be an absolute <c>https</c> URL here so a
/// malformed or unsafe reference (for example a <c>javascript:</c> or <c>data:</c> URI) never reaches
/// the browser; the client registry validates again at the render boundary (defense in depth).
/// </para>
/// <para>
/// Register via keyed DI:
/// <code>
/// services.AddKeyedSingleton&lt;ITool&gt;(RenderImageTool.ToolName, (sp, _) =&gt;
///     new RenderImageTool(sp.GetRequiredService&lt;IClientToolBridge&gt;()));
/// </code>
/// </para>
/// </remarks>
public sealed class RenderImageTool : BlockingProxyTool
{
    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
    public const string ToolName = "render_image";

    private const string Render = "render";
    private const string UrlKey = "url";

    private static readonly IReadOnlyList<string> Operations = [Render];

    /// <summary>Initializes a new instance of the <see cref="RenderImageTool"/> class.</summary>
    /// <param name="bridge">The client round-trip bridge used to delegate rendering to the browser.</param>
    public RenderImageTool(IClientToolBridge bridge) : base(bridge)
    {
    }

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Displays an image inline in your answer in the user's browser. Operation: render. " +
        "Parameters: url (string, required — an absolute https URL to the image); " +
        "alt (string, optional — accessible alt text describing the image); " +
        "caption (string, optional — a short caption shown beneath the image). " +
        "Use this when the user asks to see or display an image you can reference by URL.";

    /// <inheritdoc />
    public override IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public override Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, Render, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Fail($"Unknown operation: {operation}. Supported: {Render}"));

        if (!IsClientAttached)
            return Task.FromResult(ToolResult.Fail("No client is connected to this conversation, so an image cannot be displayed."));

        if (!parameters.TryGetValue(UrlKey, out var urlValue) || urlValue is not string url || string.IsNullOrWhiteSpace(url))
            return Task.FromResult(ToolResult.Fail("Provide an image 'url' to display."));

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult(ToolResult.Fail("The image 'url' must be an absolute https URL."));

        var argumentsJson = JsonSerializer.Serialize(parameters, SerializerOptions);

        return InvokeClientAsync(argumentsJson, "The client did not display the image in time.", cancellationToken);
    }
}
