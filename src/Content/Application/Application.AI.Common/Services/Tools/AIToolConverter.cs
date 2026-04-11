using Application.AI.Common.Interfaces.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Generic fallback converter that bridges any <see cref="ITool"/> implementation
/// to a <see cref="AITool"/> for the Microsoft.Extensions.AI chat pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This converter handles any <c>ITool</c> by exposing a two-parameter function to the LLM:
/// <c>operation</c> (which operation to invoke) and <c>parametersJson</c> (JSON-encoded
/// operation arguments). The LLM selects the operation from the list embedded in the
/// tool description and provides parameters as a JSON string.
/// </para>
/// <para>
/// <strong>Priority:</strong> 200 (generic fallback). Register tool-specific converters
/// at priority 100 for richer parameter schemas when needed.
/// </para>
/// <para>
/// <strong>Operation filtering:</strong> When <c>allowedOperations</c> is provided
/// (typically from a SKILL.md tool declaration), only the intersection with
/// <see cref="ITool.SupportedOperations"/> is exposed. This keeps the LLM focused
/// on the operations relevant to the current skill.
/// </para>
/// </remarks>
public sealed class AIToolConverter : IToolConverter
{
    private readonly ILogger<AIToolConverter> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AIToolConverter"/>.
    /// </summary>
    /// <param name="logger">Logger for conversion diagnostics.</param>
    public AIToolConverter(ILogger<AIToolConverter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public int Priority => 200;

    /// <inheritdoc />
    /// <remarks>Returns <c>true</c> for all tools — this is the generic fallback converter.</remarks>
    public bool CanConvert(ITool tool) => true;

    /// <inheritdoc />
    public AITool? Convert(ITool tool, IReadOnlyList<string>? allowedOperations = null)
    {
        var activeOperations = ResolveActiveOperations(tool, allowedOperations);
        if (activeOperations.Count == 0)
        {
            _logger.LogWarning(
                "Tool {ToolName} has no active operations after filtering (allowed: {Allowed}, supported: {Supported})",
                tool.Name,
                allowedOperations != null ? string.Join(", ", allowedOperations) : "all",
                string.Join(", ", tool.SupportedOperations));
            return null;
        }

        var description = new ToolDescriptionBuilder()
            .AddPurpose(tool.Description)
            .AddOperations(activeOperations)
            .AddParameters(
                ("operation", Required: true, $"One of: {string.Join(", ", activeOperations)}"),
                ("parametersJson", Required: false, "Object containing operation-specific arguments (e.g. {\"path\": \"src\", \"search_term\": \"foo\"})"))
            .Build();

        var aiFunction = AIFunctionFactory.Create(
            async (string operation, JsonElement? parametersJson, CancellationToken cancellationToken) =>
            {
                if (!activeOperations.Contains(operation, StringComparer.OrdinalIgnoreCase))
                {
                    return $"Error: Operation '{operation}' is not available. Valid operations: {string.Join(", ", activeOperations)}";
                }

                var parameters = ParseParameters(parametersJson);
                var result = await tool.ExecuteAsync(operation, parameters, cancellationToken);
                return result.Success
                    ? result.Output ?? "OK"
                    : $"Error: {result.Error}";
            },
            new AIFunctionFactoryOptions
            {
                Name = tool.Name,
                Description = description
            });

        _logger.LogDebug(
            "Converted tool {ToolName} to AITool with {OperationCount} operations: [{Operations}]",
            tool.Name,
            activeOperations.Count,
            string.Join(", ", activeOperations));

        return aiFunction;
    }

    /// <summary>
    /// Resolves the active operations by intersecting allowed operations with supported operations.
    /// </summary>
    private static IReadOnlyList<string> ResolveActiveOperations(
        ITool tool,
        IReadOnlyList<string>? allowedOperations)
    {
        if (allowedOperations is null or { Count: 0 })
            return tool.SupportedOperations;

        return tool.SupportedOperations
            .Where(op => allowedOperations.Contains(op, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Parses parameters from a <see cref="JsonElement"/> into a string-keyed dictionary.
    /// Handles three cases the LLM may produce:
    /// <list type="bullet">
    ///   <item><description>Object — direct parameter map (most common LLM output)</description></item>
    ///   <item><description>String — JSON-encoded object, double-decoded</description></item>
    ///   <item><description>Null / missing — returns empty dictionary</description></item>
    /// </list>
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ParseParameters(JsonElement? parametersJson)
    {
        if (parametersJson is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new Dictionary<string, object?>();

        // LLM passed a JSON object directly — the common case
        if (element.ValueKind == JsonValueKind.Object)
            return FlattenElement(element);

        // LLM passed a JSON string containing a nested JSON object
        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return new Dictionary<string, object?>();

            try
            {
                using var doc = JsonDocument.Parse(raw);
                return FlattenElement(doc.RootElement);
            }
            catch (JsonException)
            {
                return new Dictionary<string, object?> { ["raw_input"] = raw };
            }
        }

        return new Dictionary<string, object?>();
    }

    private static Dictionary<string, object?> FlattenElement(JsonElement obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var i) ? (object?)i : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText()
            };
        }
        return dict;
    }
}
