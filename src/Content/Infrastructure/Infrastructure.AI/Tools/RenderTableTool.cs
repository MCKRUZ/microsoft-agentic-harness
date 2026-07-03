using System.Text.Json;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Renders a data table inline in the agent's chat answer ("generative UI"): the agent supplies column
/// headers and rows, and the connected browser draws a real table in the transcript. The browser returns
/// a short textual acknowledgement so the agent can narrate what it showed.
/// </summary>
/// <remarks>
/// <para>
/// A <b>synchronous</b> client round-trip tool using the same blocking-proxy mechanism as
/// <see cref="RenderImageTool"/>: it delegates the render to the connected browser via
/// <see cref="IClientToolBridge"/> and returns the browser's acknowledgement in milliseconds. The table
/// is non-interactive (no user input flows back), so the agent's turn ends promptly. Only the structured
/// data flows through the model; the browser owns the markup, so the agent can never inject arbitrary
/// HTML — React escapes every cell.
/// </para>
/// <para>
/// The essential contract is validated here — <c>columns</c> must be a non-empty array — so a table
/// request that could never render fails fast. <c>rows</c> are deliberately <em>not</em> validated on
/// the server: they are best-effort data that the client's <c>parseTableArgs</c> normalizes (a null or
/// a non-array becomes an empty table; an individual malformed row is dropped and the rest render). The
/// client is the single source of row-normalization truth, so the two boundaries never disagree and the
/// server never rejects a ragged table the client would have shown. Every cell is coerced to display
/// text and escaped by React at the render boundary.
/// Register via keyed DI:
/// <code>
/// services.AddKeyedSingleton&lt;ITool&gt;(RenderTableTool.ToolName, (sp, _) =&gt;
///     new RenderTableTool(sp.GetRequiredService&lt;IClientToolBridge&gt;()));
/// </code>
/// </para>
/// </remarks>
public sealed class RenderTableTool : BlockingProxyTool
{
    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
    public const string ToolName = "render_table";

    private const string Render = "render";

    private static readonly IReadOnlyList<string> Operations = [Render];

    /// <summary>Initializes a new instance of the <see cref="RenderTableTool"/> class.</summary>
    /// <param name="bridge">The client round-trip bridge used to delegate rendering to the browser.</param>
    public RenderTableTool(IClientToolBridge bridge) : base(bridge)
    {
    }

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Displays a data table inline in your answer in the user's browser. Operation: render. " +
        "Parameters: title (string, optional — a heading for the table); " +
        "columns (array of strings, required — the column headers, left to right); " +
        "rows (array of arrays, optional — each inner array is one row of cell values aligned to the " +
        "columns; extra cells are dropped and missing cells render blank). " +
        "Use this to present tabular or structured data instead of prose or a markdown table.";

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
            return Task.FromResult(ToolResult.Fail("No client is connected to this conversation, so a table cannot be displayed."));

        // Serialize once, then validate the structure from the JSON so nested arrays are inspected
        // deterministically regardless of how the tool arguments were deserialized into the dictionary.
        var argumentsJson = JsonSerializer.Serialize(parameters, SerializerOptions);

        var validationError = ValidateColumns(argumentsJson);
        if (validationError is not null)
            return Task.FromResult(ToolResult.Fail(validationError));

        return InvokeClientAsync(argumentsJson, "The client did not display the table in time.", cancellationToken);
    }

    /// <summary>
    /// Validates that <c>columns</c> is a non-empty array; returns an error message, or null when valid.
    /// <c>rows</c> are intentionally not validated — they are normalized client-side (see the type remarks),
    /// so a null, a non-array, or an individual malformed row renders as empty/dropped rather than failing
    /// the whole table.
    /// </summary>
    private static string? ValidateColumns(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        if (!doc.RootElement.TryGetProperty("columns", out var columns)
            || columns.ValueKind != JsonValueKind.Array
            || columns.GetArrayLength() == 0)
            return "Provide a non-empty 'columns' array of header labels.";

        return null;
    }
}
