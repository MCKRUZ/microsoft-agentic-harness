namespace Application.Common.Interfaces.Common;

/// <summary>
/// Well-known directory types used by the agentic harness.
/// Consumed by <see cref="IDirectoryMapper"/> to resolve absolute paths.
/// </summary>
public enum HarnessDirectory
{
    /// <summary>Root directory for all harness data.</summary>
    Root = 0,

    /// <summary>Run-based log output (<c>{root}/logs/{runId}/</c>).</summary>
    Logs,

    /// <summary>Skill definition files (<c>{root}/skills/</c>).</summary>
    Skills,

    /// <summary>Agent manifest files (<c>{root}/manifests/</c>).</summary>
    Manifests,

    /// <summary>Per-run artifact output (<c>{root}/runs/{runId}/</c>).</summary>
    Runs,

    /// <summary>Temporary working directory for tool execution (<c>{root}/temp/</c>).</summary>
    Temp,

    /// <summary>MCP server configuration and state (<c>{root}/mcp/</c>).</summary>
    Mcp
}
