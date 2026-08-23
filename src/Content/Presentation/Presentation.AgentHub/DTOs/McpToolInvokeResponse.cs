namespace Presentation.AgentHub.DTOs;

/// <summary>Response envelope for an MCP tool invocation.</summary>
public sealed record McpToolInvokeResponse
{
    /// <summary>
    /// Sanitized, length-bounded output from the tool. Populated only when <see cref="Success"/> is
    /// <see langword="true"/>; <see langword="null"/> on failure.
    /// </summary>
    /// <remarks>
    /// A string, not a <c>JsonElement</c>, since #481: the caller-facing scrub every direct-invocation
    /// surface applies operates on text, and returning structured JSON here would mean either skipping
    /// that scrub or re-deriving it for a JSON tree — see <c>IDirectToolInvoker.InvokeMcpToolAsync</c>.
    /// </remarks>
    public string? Output { get; init; }

    /// <summary>Whether <see cref="Output"/> was cut short at the host's configured character ceiling.</summary>
    public bool OutputTruncated { get; init; }

    /// <summary>Wall-clock duration of the invocation in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary><see langword="true"/> when the tool completed without throwing.</summary>
    public bool Success { get; init; }

    /// <summary>Sanitized error message. Populated only when <see cref="Success"/> is <see langword="false"/>.</summary>
    public string? Error { get; init; }
}
