using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Tools;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Classifies tools by their concurrency safety using the tool's self-declared properties.
/// Follows a fail-closed design: tools that do not explicitly declare safety are treated
/// as write-serial to prevent accidental parallel state mutation.
/// </summary>
/// <remarks>
/// Classification logic:
/// <list type="bullet">
///   <item><see cref="ITool.IsConcurrencySafe"/> = true → <see cref="ToolConcurrencyClassification.ReadOnly"/></item>
///   <item><see cref="ITool.IsReadOnly"/> = true → <see cref="ToolConcurrencyClassification.ReadOnly"/></item>
///   <item>Otherwise → <see cref="ToolConcurrencyClassification.WriteSerial"/> (fail-closed)</item>
/// </list>
/// </remarks>
public sealed class ToolConcurrencyClassifier : IToolConcurrencyClassifier
{
    /// <inheritdoc />
    public ToolConcurrencyClassification Classify(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (tool.IsConcurrencySafe || tool.IsReadOnly)
            return ToolConcurrencyClassification.ReadOnly;

        return ToolConcurrencyClassification.WriteSerial;
    }
}
