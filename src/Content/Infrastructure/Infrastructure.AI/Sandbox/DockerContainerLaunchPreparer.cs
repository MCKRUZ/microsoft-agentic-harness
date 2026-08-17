using Application.AI.Common.Models.Sandbox;
using Docker.DotNet;
using Docker.DotNet.Models;
using Domain.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Container-launch mechanics shared by <see cref="DockerSandboxExecutor"/> (one-shot,
/// run-to-completion) and <see cref="DockerSandboxSessionFactory"/> (long-lived, duplex):
/// daemon availability, image resolution/allowlist, container hardening parameters, and
/// workspace lifecycle. Extracted so neither caller can drift from the other's security
/// posture — see #371. Not a security boundary change: every check here is identical to what
/// <see cref="DockerSandboxExecutor"/> enforced before this type existed.
/// </summary>
public sealed class DockerContainerLaunchPreparer(
    IDockerClient dockerClient,
    IOptionsMonitor<SandboxExecutionOptions> options,
    ILogger<DockerContainerLaunchPreparer> logger)
{
    /// <summary>
    /// Whether <paramref name="cpuCoreLimit"/> is usable for Docker's <c>NanoCPUs</c> field.
    /// <c>NanoCPUs = 0</c> means "unlimited" to Docker, so a non-positive (or NaN) value must be
    /// rejected as invalid input rather than silently granting the container the whole host.
    /// Shared by every caller that validates a request before spawning a container, so the check
    /// and its rationale live in one place instead of being re-derived per caller.
    /// </summary>
    public static bool IsValidCpuCoreLimit(double cpuCoreLimit) => cpuCoreLimit > 0;

    /// <summary>The rejection message for <see cref="IsValidCpuCoreLimit"/> returning false.</summary>
    public static string InvalidCpuCoreLimitMessage(double cpuCoreLimit) =>
        $"Invalid resource limits: CpuCoreLimit must be a positive number of cores (was {cpuCoreLimit}). " +
        "A non-positive value would map to NanoCPUs=0, which Docker treats as unlimited.";

    /// <summary>
    /// Dynamic-linker environment variables that let a process on the container's own image load
    /// an arbitrary shared library before <c>main</c> runs — shared with
    /// <see cref="ProcessSandboxLaunchPreparer"/>'s reserved-name list
    /// (<see cref="SandboxReservedEnvironment.DynamicLinkerNames"/>) so the two tiers cannot drift.
    /// Unlike that tier — which additionally guards against a grant un-pinning a variable
    /// inherited from the host — a container starts from a clean, image-defined environment, so
    /// there is no host-leak risk here. The risk is different but just as real: a request with
    /// <see cref="ToolCapability.FileWrite"/> gets a read-write bind mount at <c>/workspace</c>,
    /// so a caller can write a malicious <c>.so</c> there and, with an unguarded
    /// <c>LD_PRELOAD</c>/<c>LD_LIBRARY_PATH</c>/<c>LD_AUDIT</c> grant, have the container's own
    /// dynamic linker load it into the sandboxed process on start.
    /// </summary>
    private static readonly string[] ReservedContainerEnvironmentVariableNames = SandboxReservedEnvironment.DynamicLinkerNames;

    /// <summary>
    /// Returns the first per-request environment grant whose name collides (case-insensitively)
    /// with a dynamic-linker-hijack variable, or null when all grants are benign.
    /// </summary>
    public static string? FindReservedEnvironmentGrant(IReadOnlyDictionary<string, string>? environmentVariables) =>
        SandboxReservedEnvironment.FindReservedGrant(ReservedContainerEnvironmentVariableNames, environmentVariables);

    /// <summary>Pings the Docker daemon. Returns false rather than throwing on any failure — unreachability is an expected, callable-checkable condition, not an exceptional one.</summary>
    public async Task<bool> IsDockerAvailableAsync(CancellationToken ct)
    {
        try
        {
            await dockerClient.System.PingAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Docker daemon not reachable");
            return false;
        }
    }

    /// <summary>
    /// Awaits container exit. A thin pass-through to the Docker client this preparer already
    /// owns, so <see cref="DockerSandboxSession"/> does not need its own separate
    /// <see cref="IDockerClient"/> reference just for this one call.
    /// </summary>
    public Task<ContainerWaitResponse> WaitForContainerExitAsync(string containerId, CancellationToken ct) =>
        dockerClient.Containers.WaitContainerAsync(containerId, ct);

    /// <summary>
    /// Resolves the container image to run: <paramref name="requestImage"/> when the caller
    /// specified one (e.g. <see cref="Domain.AI.Sandbox.SandboxSessionRequest.ContainerImage"/> —
    /// used for a bundle-owned stdio MCP server, whose registered name contains a fresh GUID per
    /// staging and so can never match a <paramref name="toolName"/>-keyed
    /// <see cref="SandboxExecutionOptions.ToolOverrides"/> entry), else a per-tool
    /// <c>ContainerImage</c> override from <see cref="SandboxExecutionOptions.ToolOverrides"/> when
    /// one is configured, else the configured default image. Every non-default image — caller-
    /// specified or tool-override — is validated against
    /// <see cref="ContainerSandboxOptions.AllowedImagePrefixes"/>: a caller can choose among images
    /// the operator has already permitted, never escape that allowlist.
    /// </summary>
    public string ResolveImage(string toolName, string? requestImage = null)
    {
        if (!string.IsNullOrEmpty(requestImage))
        {
            ValidateImageAllowed(requestImage);
            return requestImage;
        }

        var currentOptions = options.CurrentValue;

        if (currentOptions.ToolOverrides.TryGetValue(toolName, out var toolOverride)
            && !string.IsNullOrEmpty(toolOverride.ContainerImage))
        {
            var overrideImage = toolOverride.ContainerImage;
            ValidateImageAllowed(overrideImage);
            return overrideImage;
        }

        return currentOptions.Container.DefaultImage;
    }

    private void ValidateImageAllowed(string image)
    {
        var allowedPrefixes = options.CurrentValue.Container.AllowedImagePrefixes;
        if (allowedPrefixes.Count == 0)
            return;

        foreach (var prefix in allowedPrefixes)
        {
            if (image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return;
        }

        throw new InvalidOperationException(
            $"Image '{image}' not in allowed registry list. Allowed prefixes: {string.Join(", ", allowedPrefixes)}");
    }

    /// <summary>Pulls <paramref name="image"/> if the daemon does not already have it locally.</summary>
    public async Task EnsureImageAvailableAsync(string image, CancellationToken ct)
    {
        try
        {
            await dockerClient.Images.InspectImageAsync(image, ct);
        }
        catch (DockerImageNotFoundException)
        {
            logger.LogInformation("Pulling image {Image}", image);
            await dockerClient.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image },
                null,
                new Progress<JSONMessage>(),
                ct);
        }
    }

    /// <summary>
    /// Builds the hardened container configuration shared by every backend consumer: unprivileged
    /// user, dropped capabilities, no new privileges, read-only root filesystem, and a workspace
    /// bind mount scoped read-write or read-only by <see cref="ToolCapability.FileWrite"/>.
    /// </summary>
    /// <param name="interactive">
    /// When true, opens stdin and attaches all three standard streams without a TTY — the shape
    /// <see cref="DockerSandboxSessionFactory"/> needs to hold a live, bidirectional conversation
    /// with the container's main process. The one-shot executor never sets this: its input goes
    /// in via a bind-mounted file, not stdin.
    /// </param>
    /// <param name="environmentVariables">
    /// Variables to set in the container's environment, in addition to whatever the image itself
    /// defines. Unlike <see cref="ProcessSandboxLaunchPreparer"/>'s process environment, a
    /// container starts from a clean, image-defined environment rather than an inherited host
    /// one — so there is no host-secret-leak risk to guard against here — but the caller must
    /// still have rejected a dynamic-linker-hijack grant via
    /// <see cref="FindReservedEnvironmentGrant"/> before calling this method; that check is a
    /// different threat model (linker hijack via a writable bind mount, not host inheritance) and
    /// this method does not re-derive it.
    /// </param>
    public CreateContainerParameters BuildContainerParams(
        string? command,
        IReadOnlyList<string>? argumentList,
        ResourceLimits limits,
        ToolPermissionProfile permissionProfile,
        string workspaceDir,
        string image,
        bool interactive = false,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        string? workingDirectory = null)
    {
        // EffectiveCapabilities, not RequiredCapabilities — a per-tool DeniedCapabilities override
        // must genuinely restrict what the container is provisioned with (#405).
        var hasNetworkAccess = permissionProfile.EffectiveCapabilities.HasFlag(ToolCapability.NetworkAccess);

        List<string>? cmd = null;
        if (command is not null)
        {
            cmd = [command];
            if (argumentList is { Count: > 0 })
                cmd.AddRange(argumentList);
        }

        return new CreateContainerParameters
        {
            Image = image,
            Cmd = cmd,
            WorkingDir = workingDirectory,
            Env = environmentVariables is { Count: > 0 }
                ? environmentVariables.Select(kvp => $"{kvp.Key}={kvp.Value}").ToList()
                : null,
            User = "65534:65534",
            AttachStdin = interactive,
            AttachStdout = interactive,
            AttachStderr = interactive,
            OpenStdin = interactive,
            StdinOnce = false,
            Tty = false,
            HostConfig = new HostConfig
            {
                Memory = limits.MemoryLimitBytes,
                // CPU cap alongside the memory cap: an unlimited container can starve the
                // host. NanoCPUs is the core count scaled by 1e9 (Docker's CpuQuota/CpuPeriod
                // shorthand); 1.0 core by default, callers opt into more via ResourceLimits.
                NanoCPUs = (long)(limits.CpuCoreLimit * 1_000_000_000),
                NetworkMode = hasNetworkAccess ? "bridge" : "none",
                ReadonlyRootfs = true,
                AutoRemove = false,
                Binds = [permissionProfile.EffectiveCapabilities.HasFlag(ToolCapability.FileWrite)
                    ? $"{workspaceDir}:/workspace:rw"
                    : $"{workspaceDir}:/workspace:ro"],
                // ReadonlyRootfs plus a possibly read-only /workspace bind would otherwise leave NO
                // writable path anywhere in the container. Most real programs (npm/pip caches, lock
                // files, a temp file mid-write) touch /tmp unconditionally regardless of whether
                // ToolCapability.FileWrite was granted, so this tmpfs is unconditional too — sized
                // off the same ResourceLimits.DiskQuotaBytes the workspace bind is implicitly bounded
                // by, capped by the tmpfs's own noexec/nosuid mount options. Security-review note:
                // this means ToolCapability.FileWrite governs writes to the /workspace bind
                // specifically, not "can this container write anything at all" — every Docker-tier
                // container, regardless of that capability, gets 100 MB of ephemeral, non-executable,
                // per-container scratch space that is destroyed with the container and never shared
                // with the host or another container.
                Tmpfs = new Dictionary<string, string> { ["/tmp"] = $"rw,noexec,nosuid,size={limits.DiskQuotaBytes}" },
                PidsLimit = limits.MaxSubprocesses,
                SecurityOpt = ["no-new-privileges:true"],
                CapDrop = ["ALL"]
            }
        };
    }

    /// <summary>
    /// Shared parent directory every Docker-tier workspace nests under, one level below
    /// <see cref="Path.GetTempPath"/> (which is typically world-listable, e.g. mode 1777 on Linux).
    /// </summary>
    /// <remarks>
    /// A seeded workspace's own directory is deliberately other-readable so the container's fixed
    /// unprivileged UID can traverse it (see <see cref="SandboxWorkspace.SetContainerAccessiblePermissions"/>),
    /// but that says nothing about whether another local account can find its unguessable GUID name in
    /// the first place. This parent is other-EXECUTABLE (traverse) but not other-READABLE (list), so a
    /// local account without the exact GUID cannot enumerate live bundle-owned workspace names — only
    /// walk into one it already knows.
    /// </remarks>
    private const string WorkspaceParentDirName = "agentic-harness-sandbox";

    /// <summary>Creates a fresh, restrictively-permissioned temp directory bind-mounted into the container as <c>/workspace</c>.</summary>
    public string CreateWorkspace()
    {
        var parent = Path.Combine(Path.GetTempPath(), WorkspaceParentDirName);
        Directory.CreateDirectory(parent);
        SandboxWorkspace.SetTraverseOnlyPermissions(parent);

        var workspaceDir = Path.Combine(parent, $"docker-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceDir);
        SandboxWorkspace.SetRestrictivePermissions(workspaceDir);
        return workspaceDir;
    }

    /// <summary>
    /// Seeds an already-created <paramref name="workspaceDir"/> from <paramref name="seedDirectory"/>
    /// and widens its permissions so the container's own fixed unprivileged UID can read what was
    /// just seeded into it.
    /// </summary>
    /// <remarks>
    /// Owned here — one call, through the same class that already owns <see cref="CreateWorkspace"/>
    /// and <see cref="CleanupWorkspace"/> — rather than left as a two-step "seed, then widen, in that
    /// exact order" ritual for each call site to reimplement correctly. A prior version of #371 did
    /// leave it as a bare two-line sequence at the one call site that needed it; caught in review as
    /// a structural risk (an easy order-of-operations mistake for a future second call site to make),
    /// not a concrete bug in the one call site that existed. The widening must stay scoped to exactly
    /// a workspace this method actually seeded — never applied unconditionally to every Docker-tier
    /// workspace regardless of whether it holds seeded content, which a still-earlier version did and
    /// /code-review caught as broader host-filesystem exposure than the problem it solved (see
    /// <see cref="SandboxWorkspace.SetContainerAccessiblePermissions"/>'s own remarks).
    /// </remarks>
    public void SeedWorkspace(string workspaceDir, string seedDirectory)
    {
        SandboxWorkspace.SeedFrom(seedDirectory, workspaceDir);
        SandboxWorkspace.SetContainerAccessiblePermissions(workspaceDir);
    }

    /// <summary>Best-effort recursive delete of a workspace created by <see cref="CreateWorkspace"/>. Never throws.</summary>
    public void CleanupWorkspace(string path) => SandboxWorkspace.Cleanup(path, logger, "Docker");

    /// <summary>
    /// Stops a container with the configured grace period before Docker force-kills it. Used on
    /// timeout, where the process/session inside deserves a chance to exit cleanly before removal.
    /// </summary>
    public async Task StopContainerGracefullyAsync(string containerId, CancellationToken ct)
    {
        var gracePeriod = options.CurrentValue.Container.StopGracePeriodSeconds;
        logger.LogWarning("Container {ContainerId} timed out, stopping with {GracePeriod}s grace period",
            containerId, gracePeriod);

        try
        {
            await dockerClient.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = (uint)gracePeriod }, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Stop container after timeout failed (may already be removed)");
        }
    }

    /// <summary>
    /// Force-kills and removes the container on a dedicated cleanup token. The caller's token is
    /// deliberately NOT used: when an execution/session is cancelled or times out, that token is
    /// already cancelled, and using it here would abort the removal call and leak the container
    /// running unbounded on the host. Cleanup gets its own bounded window
    /// (<c>ContainerSandboxOptions.CleanupTimeoutSeconds</c>) instead.
    /// </summary>
    public async Task RemoveContainerSafeAsync(string? containerId)
    {
        if (containerId is null)
            return;

        using var cleanupCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(options.CurrentValue.Container.CleanupTimeoutSeconds));

        try
        {
            await dockerClient.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true }, cleanupCts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Container {ContainerId} removal failed — it may still be running on the host", containerId);
        }
    }
}
