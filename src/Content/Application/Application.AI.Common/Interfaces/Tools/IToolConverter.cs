using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.Tools;

/// <summary>
/// Strategy pattern interface for converting <see cref="ITool"/> implementations
/// into <see cref="AITool"/> instances for the Microsoft.Extensions.AI chat pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Each tool type gets its own converter that knows how to build the parameter class
/// and delegate for <c>AIFunctionFactory.Create</c>. The parameter class drives
/// JSON Schema generation — its properties become the schema the LLM sees.
/// </para>
/// <para>
/// Converters are registered in DI and iterated by priority. The first converter
/// that returns true from <see cref="CanConvert"/> handles the tool.
/// </para>
/// <para>
/// <strong>Registration pattern:</strong>
/// <code>
/// services.AddSingleton&lt;IToolConverter, FileSystemToolConverter&gt;();
/// services.AddSingleton&lt;IToolConverter, McpToolConverter&gt;();
/// </code>
/// </para>
/// </remarks>
public interface IToolConverter
{
    /// <summary>
    /// Gets the priority order for this converter. Lower values are checked first.
    /// Default convention: 100 for standard tools, 200 for generic fallback.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Determines whether this converter can handle the given tool.
    /// </summary>
    /// <param name="tool">The tool to check.</param>
    /// <returns><c>true</c> if this converter can produce an <see cref="AITool"/> for the given tool.</returns>
    bool CanConvert(ITool tool);

    /// <summary>
    /// Converts the tool into an <see cref="AITool"/> for the chat pipeline.
    /// </summary>
    /// <param name="tool">The tool to convert.</param>
    /// <param name="allowedOperations">
    /// Optional subset of operations to expose. When null, all <see cref="ITool.SupportedOperations"/> are available.
    /// Populated from the SKILL.md tool declaration's <c>operations</c> field.
    /// </param>
    /// <returns>The converted <see cref="AITool"/>, or <c>null</c> if conversion fails.</returns>
    AITool? Convert(ITool tool, IReadOnlyList<string>? allowedOperations = null);
}
