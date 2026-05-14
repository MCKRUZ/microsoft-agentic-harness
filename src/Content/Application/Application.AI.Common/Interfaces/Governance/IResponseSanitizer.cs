using Domain.AI.Governance;

namespace Application.AI.Common.Interfaces.Governance;

/// <summary>
/// Sanitizes a single concern category from MCP tool output.
/// Implementations handle one specific threat type (credentials, injection, exfiltration).
/// </summary>
public interface IResponseSanitizer
{
    /// <summary>Gets the category of threats this sanitizer detects.</summary>
    SanitizationCategory Category { get; }

    /// <summary>
    /// Scans content for threats and returns sanitized output with findings.
    /// </summary>
    /// <param name="content">The tool output to scan.</param>
    /// <param name="toolName">Optional tool name for context-aware scanning.</param>
    SanitizationResult Sanitize(string content, string? toolName = null);
}
