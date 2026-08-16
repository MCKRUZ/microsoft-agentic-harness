using System.Diagnostics;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Subprocess-launch mechanics shared by <see cref="ProcessSandboxExecutor"/> (one-shot,
/// run-to-completion) and <see cref="ProcessSandboxSessionFactory"/> (long-lived, duplex):
/// program allowlist enforcement, environment isolation, workspace lifecycle, and Windows Job
/// Object resource limits. Extracted so neither caller can drift from the other's security
/// posture — see #371. Not a security boundary change: every check here is identical to what
/// <see cref="ProcessSandboxExecutor"/> enforced before this type existed.
/// </summary>
/// <remarks>
/// This class is public only because it appears as a constructor parameter on the public
/// <see cref="ProcessSandboxExecutor"/> and <see cref="ProcessSandboxSessionFactory"/> (a type
/// cannot be less accessible than a public member's parameter type). That does not make every
/// member of this class part of the intended external surface — see
/// <see cref="CreateWorkspaceDirectory"/>'s narrower setter for the one member where the
/// distinction matters.
/// </remarks>
public sealed class ProcessSandboxLaunchPreparer(
    IProcessResourceLimiter resourceLimiter,
    IOptionsMonitor<SandboxConfig> sandboxConfig,
    ILogger<ProcessSandboxLaunchPreparer> logger)
{
    /// <summary>
    /// Environment variable names that per-request grants may never override, compared
    /// case-insensitively (Windows environment lookups ignore case). Covers the pinned temp
    /// set (always redirected into the workspace), the security-critical variables the
    /// allowlist controls — a grant of <c>temp</c>, <c>Path</c>, or <c>COMSPEC</c> would
    /// otherwise un-pin or re-smuggle them — and the dynamic-linker variables
    /// (<see cref="DockerContainerLaunchPreparer"/> guards the same names for the container
    /// tier). This tier needs the guard even more: it runs as the harness's own OS user against
    /// a fully writable host filesystem, so an unguarded grant loads an arbitrary shared library
    /// into an otherwise operator-allowlisted program — the allowlist checks <c>Command</c> only,
    /// and never meaningfully ran if the attacker's constructor executed first.
    /// </summary>
    private static readonly string[] ReservedEnvironmentVariableNames =
    [
        "TEMP", "TMP", "TMPDIR", "PATH", "COMSPEC", "PATHEXT", "SYSTEMROOT",
        "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "LD_ORIGIN_PATH",
        "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH", "DYLD_FRAMEWORK_PATH"
    ];

    /// <summary>
    /// Test seam: when set, overrides workspace creation instead of <see cref="CreateDefaultWorkspace"/>.
    /// Null (the default) means "use the real temp-directory-backed workspace." The setter is
    /// deliberately narrower than the class's own (necessarily public — see the class remarks)
    /// visibility: only this assembly and its test assembly (<c>InternalsVisibleTo</c>) may
    /// redirect where a sandboxed process's workspace is created.
    /// </summary>
    public Func<string>? CreateWorkspaceDirectory { get; internal set; }

    /// <summary>
    /// Returns the first per-request environment grant whose name collides (case-insensitively)
    /// with a reserved variable, or null when all grants are benign.
    /// </summary>
    public static string? FindReservedEnvironmentGrant(IReadOnlyDictionary<string, string>? environmentVariables)
    {
        if (environmentVariables is null)
            return null;

        return environmentVariables.Keys.FirstOrDefault(
            name => ReservedEnvironmentVariableNames.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Creates the workspace directory (via the <see cref="CreateWorkspaceDirectory"/> test seam, when set).</summary>
    public string CreateWorkspace() => (CreateWorkspaceDirectory ?? CreateDefaultWorkspace)();

    private string CreateDefaultWorkspace()
    {
        var root = sandboxConfig.CurrentValue.WorkspaceRoot;
        var baseDir = !string.IsNullOrEmpty(root) ? root : Path.GetTempPath();

        if (!Path.IsPathRooted(baseDir))
            throw new InvalidOperationException(
                $"SandboxConfig.WorkspaceRoot must be an absolute path. Found: '{baseDir}'");

        var dir = Path.Combine(baseDir, $"sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        SandboxWorkspace.SetRestrictivePermissions(dir);
        return dir;
    }

    /// <summary>Best-effort recursive delete of a workspace created by <see cref="CreateWorkspace"/>. Never throws.</summary>
    public void CleanupWorkspace(string path) => SandboxWorkspace.Cleanup(path, logger, "process");

    /// <summary>
    /// Enforces the closed-by-default program allowlist, rebuilds an isolated environment, and
    /// starts the process. Throws <see cref="UnauthorizedAccessException"/> when the command is
    /// not on <paramref name="permissionProfile"/>'s allowlist — callers must not spawn on a
    /// caller-controlled command without this check passing first.
    /// </summary>
    public Process StartProcess(
        string command,
        IReadOnlyList<string>? argumentList,
        ToolPermissionProfile permissionProfile,
        IReadOnlyDictionary<string, string>? environmentVariables,
        string workspaceDir)
    {
        if (permissionProfile.AllowedPrograms.Count == 0)
            throw new UnauthorizedAccessException(
                "No allowed programs configured in the permission profile. Sandbox is closed-by-default.");

        if (!permissionProfile.AllowedPrograms.Contains(command, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Command '{command}' is not in the allowed programs list");

        var psi = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = workspaceDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        ConfigureIsolatedEnvironment(psi, environmentVariables, workspaceDir);

        if (argumentList is { Count: > 0 })
        {
            foreach (var arg in argumentList)
                psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    /// <summary>
    /// Rebuilds the child process environment from scratch: only variables named in the
    /// configured allowlist are copied from the host, temp variables are pinned to the
    /// disposable workspace, and pre-validated per-request grants are applied last (grants
    /// colliding with reserved names must already be rejected by the caller via
    /// <see cref="FindReservedEnvironmentGrant"/> before this runs, so they can never un-pin
    /// these values).
    /// </summary>
    /// <remarks>
    /// This is environment-level isolation only, and it is deliberately documented as
    /// partial: the child still runs as the same OS user with the same token (no privilege
    /// drop), so it can read anything the host user can read through the file system. PATH
    /// is copied verbatim by default (cmd/child executable resolution needs it), which leaks
    /// host directory layout and carries binary-planting risk if PATH contains
    /// user-writable directories — operators can remove PATH from
    /// <c>SandboxConfig.ProcessEnvironmentAllowlist</c> when tools do not need it. Use
    /// container isolation for a real security boundary.
    /// </remarks>
    private void ConfigureIsolatedEnvironment(
        ProcessStartInfo psi, IReadOnlyDictionary<string, string>? environmentVariables, string workspaceDir)
    {
        // Closed-by-default: drop everything inherited from the host process.
        psi.EnvironmentVariables.Clear();

        foreach (var name in sandboxConfig.CurrentValue.ProcessEnvironmentAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
                psi.EnvironmentVariables[name] = value;
        }

        // Temp always points inside the per-execution workspace (deleted after the run),
        // never at the host temp directory — regardless of the allowlist contents.
        psi.EnvironmentVariables["TEMP"] = workspaceDir;
        psi.EnvironmentVariables["TMP"] = workspaceDir;
        psi.EnvironmentVariables["TMPDIR"] = workspaceDir;

        if (environmentVariables is not null)
        {
            foreach (var (name, value) in environmentVariables)
                psi.EnvironmentVariables[name] = value;
        }
    }

    /// <summary>
    /// Applies Job Object resource limits to an already-started process. Throws
    /// <see cref="PlatformNotSupportedException"/> (after killing the process) when the
    /// platform has no limiter support at all — callers must not run an unlimited process on a
    /// platform that cannot bound it.
    /// </summary>
    public void ApplyResourceLimits(Process process, ResourceLimits limits)
    {
        if (!resourceLimiter.Apply(process, limits))
        {
            if (!resourceLimiter.IsSupported)
            {
                KillProcess(process);
                throw new PlatformNotSupportedException(
                    "Process resource limits are not available on this platform. " +
                    "Use container isolation (SandboxIsolationLevel.Container) for cross-platform enforcement.");
            }

            logger.LogWarning("Failed to apply resource limits to process {ProcessId}", process.Id);
        }
    }

    /// <summary>Force-kills a process (and its tree). Safe to call on one that has already exited.</summary>
    public void KillProcess(Process process)
    {
        logger.LogWarning("Process {ProcessId} timed out or was stopped, killing", process.Id);
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    /// <summary>
    /// Releases the Job Object handle for a completed/terminated process. Callers must invoke
    /// this once a constrained process has exited (and its usage, if needed, has been read);
    /// otherwise each execution leaks a kernel handle until host shutdown.
    /// </summary>
    public void ReleaseResourceLimiter(int processId) => resourceLimiter.Release(processId);

    /// <summary>Reads the resource usage the limiter recorded for a process and stamps it with the caller's measured wall-clock duration.</summary>
    public ResourceUsage BuildUsage(int processId, TimeSpan elapsed)
    {
        var limiterUsage = resourceLimiter.GetUsage(processId);
        if (limiterUsage is not null)
            return limiterUsage with { WallClockDuration = elapsed };

        return new ResourceUsage { WallClockDuration = elapsed };
    }
}
