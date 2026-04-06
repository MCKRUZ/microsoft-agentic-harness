namespace Application.Common.Interfaces.Common;

/// <summary>
/// Well-known directory types used by any application built on this template.
/// Consumed by <see cref="IDirectoryMapper"/> to resolve absolute paths.
/// </summary>
/// <remarks>
/// AI-specific directories (Skills, Manifests, Mcp) are defined in
/// <see cref="Domain.AI.Enums.AgentDirectory"/>.
/// </remarks>
public enum AppDirectory
{
    /// <summary>Root directory for all application data.</summary>
    Root = 0,

    /// <summary>Run-based log output (<c>{root}/logs/{runId}/</c>).</summary>
    Logs,

    /// <summary>Per-run artifact output (<c>{root}/runs/{runId}/</c>).</summary>
    Runs,

    /// <summary>Temporary working directory (<c>{root}/temp/</c>).</summary>
    Temp
}
