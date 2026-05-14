using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Chains multiple <see cref="IResponseSanitizer"/> implementations in sequence,
/// accumulating findings and producing a merged <see cref="SanitizationResult"/>.
/// </summary>
public interface ICompositeResponseSanitizer
{
    /// <summary>
    /// Runs all registered sanitizers in order against the content.
    /// </summary>
    /// <param name="content">The tool output to scan.</param>
    /// <param name="toolName">Optional tool name for context-aware scanning.</param>
    SanitizationResult Sanitize(string content, string? toolName = null);
}
