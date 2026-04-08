using Application.AI.Common.Interfaces.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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
                ("parametersJson", Required: false, "JSON object with operation-specific arguments"))
            .Build();

        var aiFunction = AIFunctionFactory.Create(
            async (string operation, string? parametersJson, CancellationToken cancellationToken) =>
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
    /// Parses JSON parameters string into a dictionary, returning an empty dictionary for null/empty input.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ParseParameters(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
            return new Dictionary<string, object?>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(parametersJson)
                   ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>
            {
                ["raw_input"] = parametersJson
            };
        }
    }
}
