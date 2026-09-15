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

    /// <summary>
    /// Per-tool sandbox configuration overrides, keyed by tool name (case-insensitively — see remarks).
    /// </summary>
    /// <remarks>
    /// The identical sibling gap to <c>Domain.Common.Config.AI.Sandbox.SandboxConfig.ToolOverrides</c>
    /// (#655 altitude follow-up): <c>DockerContainerLaunchPreparer.ResolveImage</c> looks a tool name up
    /// in this dictionary to pick its container image, and that name is resolved elsewhere
    /// case-insensitively (<c>FirstPartyToolLookup</c>) — an operator-authored entry whose casing
    /// differs from the actual registration key must still be found. Enforced the same way: a custom
    /// <see langword="init"/> accessor rebuilds whatever is assigned with an
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> comparer, so the guarantee survives an
    /// object-initializer replacing this property wholesale, not just this property's own default value.
    /// </remarks>
    public IReadOnlyDictionary<string, ToolSandboxOverride> ToolOverrides
    {
        get => _toolOverrides;
        init => _toolOverrides = new Dictionary<string, ToolSandboxOverride>(value, StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyDictionary<string, ToolSandboxOverride> _toolOverrides =
        new Dictionary<string, ToolSandboxOverride>(StringComparer.OrdinalIgnoreCase);
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
    /// Maximum time in seconds allowed for container cleanup (force kill + remove) after an
    /// execution ends. Cleanup runs on its own token so a cancelled or timed-out execution
    /// still removes its container instead of leaking it running on the host.
    /// </summary>
    public int CleanupTimeoutSeconds { get; init; } = 30;

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
