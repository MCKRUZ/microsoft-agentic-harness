namespace Application.AI.Common.Models.Sandbox;

/// <summary>
/// Configuration for sandbox execution environments (container/Docker settings).
/// Bound from <c>AppConfig:AI:Sandbox</c> configuration section.
/// Named <c>SandboxExecutionOptions</c> to distinguish from the Domain-layer
/// <see cref="Domain.Common.Config.AI.Sandbox.SandboxOptions"/> which holds
/// system-level sandbox policy (resource limits, isolation defaults).
/// </summary>
public sealed class SandboxExecutionOptions
{
    /// <summary>Configuration section path.</summary>
    public const string SectionName = "AI:Sandbox";

    /// <summary>Container (Docker) sandbox configuration.</summary>
    public ContainerSandboxOptions Container { get; init; } = new();

    /// <summary>Per-tool sandbox configuration overrides, keyed by tool name.</summary>
    public IReadOnlyDictionary<string, ToolSandboxOverride> ToolOverrides { get; init; }
        = new Dictionary<string, ToolSandboxOverride>();
}

/// <summary>
/// Container-specific sandbox configuration.
/// </summary>
public sealed class ContainerSandboxOptions
{
    /// <summary>Default container image for sandboxed execution.</summary>
    public string DefaultImage { get; init; } = "mcr.microsoft.com/dotnet/runtime:10.0";

    /// <summary>Docker daemon endpoint. Null for auto-discovery (npipe on Windows, unix socket on Linux).</summary>
    public string? DockerEndpoint { get; init; }

    /// <summary>Grace period in seconds before force-killing a container on timeout.</summary>
    public int StopGracePeriodSeconds { get; init; } = 10;

    /// <summary>
    /// Allowed image registry prefixes. Only images starting with one of these
    /// prefixes can be used. Defaults to Microsoft Container Registry only.
    /// Add additional prefixes in appsettings to allow other registries.
    /// </summary>
    public IReadOnlyList<string> AllowedImagePrefixes { get; init; } = ["mcr.microsoft.com/"];
}

/// <summary>
/// Per-tool sandbox configuration override.
/// </summary>
public sealed class ToolSandboxOverride
{
    /// <summary>Container image override for this specific tool.</summary>
    public string? ContainerImage { get; init; }
}
