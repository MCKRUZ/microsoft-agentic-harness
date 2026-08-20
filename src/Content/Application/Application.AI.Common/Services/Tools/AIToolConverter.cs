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
                    return new ConvertedToolFailure(
                        $"Error: Operation '{operation}' is not available. Valid operations: {string.Join(", ", activeOperations)}");
                }

                var parameters = ToolParameters.FromJson(parametersJson);
                var result = await tool.ExecuteAsync(operation, parameters, cancellationToken);
                return result.Success
                    ? (object)(result.Output ?? "OK")
                    : new ConvertedToolFailure($"Error: {result.Error}");
            },
            new AIFunctionFactoryOptions
            {
                Name = tool.Name,
                Description = description,
                // A ConvertedToolFailure must reach GovernedAIFunction as the CLR type it is — the
                // framework's default marshaling JSON-serializes the delegate's return value before
                // any decorator sees it, which would erase that identity. Only the marker bypasses
                // it; the success string is re-serialized exactly as the default path would, so the
                // model-facing shape for a genuine success is unchanged.
                MarshalResult = (result, _, _) => new ValueTask<object?>(
                    result is ConvertedToolFailure
                        ? result
                        : JsonSerializer.SerializeToElement((string)result!)),
                // The delegate's two branches return different CLR types (a plain string on
                // success, ConvertedToolFailure on failure) by design, so the compiler infers a
                // common Task<object> rather than Task<string> — which would otherwise make this
                // function advertise a generic/permissive return schema instead of the plain-text
                // one a genuine success actually has. Nothing reads AIFunction.ReturnJsonSchema
                // today, but excluding it is honest about that rather than leaving a misleading
                // schema as an accidental side effect of the marker mechanism above.
                ExcludeResultSchema = true
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

}
