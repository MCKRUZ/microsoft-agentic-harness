using Domain.AI.Tools;

namespace Application.AI.Common.Interfaces.Tools;

/// <summary>
/// Classifies tools by their concurrency safety for batched execution.
/// Used by <see cref="IToolExecutionStrategy"/> to partition tool calls into
/// parallel (read-only) and serial (write) groups.
/// </summary>
/// <remarks>
/// The default implementation uses <see cref="ITool.IsReadOnly"/> and
/// <see cref="ITool.IsConcurrencySafe"/> to determine classification.
/// Custom implementations can use tool metadata, operation type, or
/// external configuration to override classifications.
/// </remarks>
public interface IToolConcurrencyClassifier
{
    /// <summary>
    /// Classifies the concurrency safety of a tool.
    /// </summary>
    /// <param name="tool">The tool to classify.</param>
    /// <returns>The concurrency classification determining execution strategy.</returns>
    ToolConcurrencyClassification Classify(ITool tool);
}
