namespace Domain.Common.Config.AI.Sandbox;

/// <summary>
/// Default Docker container configuration for container-isolated sandbox execution.
/// Bound from <c>AppConfig:AI:Sandbox:ContainerDefaults</c> in appsettings.json.
/// </summary>
public sealed class ContainerDefaultsConfig
{
    /// <summary>Default Docker image for sandboxed containers.</summary>
    public string DefaultImage { get; set; } = "mcr.microsoft.com/dotnet/runtime:10.0-alpine";

    /// <summary>Docker network mode. Use "none" for full network isolation.</summary>
    public string NetworkMode { get; set; } = "none";

    /// <summary>Mount root filesystem as read-only to prevent container escape.</summary>
    public bool ReadonlyRootfs { get; set; } = true;

    /// <summary>Auto-remove container after exit to prevent resource leaks.</summary>
    public bool AutoRemove { get; set; } = true;

    /// <summary>Container-side mount path for the workspace directory.</summary>
    public string WorkspaceMountPath { get; set; } = "/workspace";

    /// <summary>Grace period in seconds before hard-killing a timed-out container.</summary>
    public int KillGracePeriodSeconds { get; set; } = 10;
}
