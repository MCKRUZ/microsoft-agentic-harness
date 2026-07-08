using System.Diagnostics;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Egress;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Executes tools as subprocesses with stdin/stdout JSON communication
/// and OS-level resource limits via <see cref="IProcessResourceLimiter"/>.
/// On Windows, resource limits use Job Objects. On other platforms,
/// execution works but limits are skipped with a logged warning.
/// </summary>
public sealed class ProcessSandboxExecutor : ISandboxExecutor
{
    private readonly IProcessResourceLimiter _resourceLimiter;
    private readonly IAttestationService _attestationService;
    private readonly ILogger<ProcessSandboxExecutor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<SandboxConfig> _sandboxConfig;
    private readonly ISandboxEgressPreflight? _egressPreflight;

    public ProcessSandboxExecutor(
        IProcessResourceLimiter resourceLimiter,
        IAttestationService attestationService,
        ILogger<ProcessSandboxExecutor> logger,
        TimeProvider timeProvider,
        IOptionsMonitor<SandboxConfig> sandboxConfig,
        ISandboxEgressPreflight? egressPreflight = null)
    {
        _resourceLimiter = resourceLimiter;
        _attestationService = attestationService;
        _logger = logger;
        _timeProvider = timeProvider;
        _sandboxConfig = sandboxConfig;
        _egressPreflight = egressPreflight;
        CreateWorkspaceDirectory = CreateDefaultWorkspace;
    }

    internal Func<string> CreateWorkspaceDirectory { get; set; }

    private string CreateDefaultWorkspace()
    {
        var root = _sandboxConfig.CurrentValue.WorkspaceRoot;
        var baseDir = !string.IsNullOrEmpty(root) ? root : Path.GetTempPath();

        if (!Path.IsPathRooted(baseDir))
            throw new InvalidOperationException(
                $"SandboxConfig.WorkspaceRoot must be an absolute path. Found: '{baseDir}'");

        var dir = Path.Combine(baseDir, $"sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        SetRestrictivePermissions(dir);
        return dir;
    }

    private static void SetRestrictivePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Environment variable names that per-request grants may never override, compared
    /// case-insensitively (Windows environment lookups ignore case). Covers the pinned temp
    /// set (always redirected into the workspace) and the security-critical variables the
    /// allowlist controls — a grant of <c>temp</c>, <c>Path</c>, or <c>COMSPEC</c> would
    /// otherwise un-pin or re-smuggle them.
    /// </summary>
    private static readonly string[] ReservedEnvironmentVariableNames =
    [
        "TEMP", "TMP", "TMPDIR", "PATH", "COMSPEC", "PATHEXT", "SYSTEMROOT"
    ];

    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
        if (!_sandboxConfig.CurrentValue.Enabled)
            throw new InvalidOperationException("Sandbox execution is disabled by configuration (Sandbox:Enabled=false).");

        if (FindReservedEnvironmentGrant(request) is { } reservedGrant)
            return await RejectReservedGrantAsync(request, reservedGrant, ct);

        var egress = await RunEgressPreflightAsync(request, ct);
        if (egress.Blocked is { } block)
            return block;

        var workspaceDir = CreateWorkspaceDirectory();
        var startTimestamp = _timeProvider.GetTimestamp();
        int? limitedProcessId = null;

        try
        {
            using var process = StartProcess(request, workspaceDir);
            ApplyResourceLimits(process, request.Limits);
            limitedProcessId = process.Id;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.StandardInput.WriteAsync(request.Input);
            process.StandardInput.Close();

            bool timedOut = false;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(request.Timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                KillProcess(process);
            }

            var (stdout, stderr) = await DrainOutputAsync(stdoutTask, stderrTask);
            var elapsed = _timeProvider.GetElapsedTime(startTimestamp);

            if (timedOut)
                return await BuildTimeoutResultAsync(process.Id, request, elapsed, egress.Digest, ct);

            if (process.ExitCode != 0)
                return await BuildCrashResultAsync(process.Id, process.ExitCode, stdout, stderr, request, elapsed, egress.Digest, ct);

            return await BuildSuccessResultAsync(process.Id, stdout, request, elapsed, egress.Digest, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Process sandbox execution failed for tool {ToolName}", request.ToolName);

            var attestation = await SignFailureAsync(
                request.ToolName, request.Input, $"Execution failed: {ex.Message}", egress.Digest, ct);

            return new SandboxExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Attestation = attestation
            };
        }
        finally
        {
            // Release the Job Object handle for this process now that its usage has been read
            // (BuildUsage runs inside the try, before this finally). Without this, every Windows
            // sandbox execution leaks one kernel handle until host shutdown.
            if (limitedProcessId is { } pid)
                _resourceLimiter.Release(pid);

            CleanupWorkspace(workspaceDir);
        }
    }

    private async Task<(SandboxExecutionResult? Blocked, string? Digest)> RunEgressPreflightAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
        if (_egressPreflight is null || request.EgressPrecheckTargets is not { Count: > 0 } targets)
        {
            return (null, null);
        }

        var decisions = await _egressPreflight.EvaluateAsync(targets, ct);
        var digest = _egressPreflight.ComputeDigest(decisions);

        var denied = decisions.FirstOrDefault(d => !d.Allowed);
        if (denied is not null)
        {
            _logger.LogWarning(
                "Sandbox refused to spawn process for tool {ToolName}: egress preflight denied '{Host}'",
                request.ToolName, denied.Target.Host);

            var attestation = await _attestationService.SignAsync(
                Domain.AI.Attestation.AttestationRequest.Failure(
                    request.ToolName,
                    request.Input,
                    $"Egress preflight denied: {denied.Target} ({denied.Reason})",
                    egressDigest: digest),
                ct);

            return (new SandboxExecutionResult
            {
                Success = false,
                ErrorMessage = $"Egress preflight denied: {denied.Target}",
                Attestation = attestation
            }, digest);
        }

        return (null, digest);
    }

    private Task<Domain.AI.Attestation.ToolExecutionAttestation> SignFailureAsync(
        string toolName, string input, string failureReason, string? egressDigest, CancellationToken ct)
        => _attestationService.SignAsync(
            Domain.AI.Attestation.AttestationRequest.Failure(toolName, input, failureReason, egressDigest: egressDigest),
            ct);

    private Task<Domain.AI.Attestation.ToolExecutionAttestation> SignSuccessAsync(
        string toolName, string input, string output, string? egressDigest, CancellationToken ct)
        => _attestationService.SignAsync(
            Domain.AI.Attestation.AttestationRequest.Success(toolName, input, output, egressDigest),
            ct);

    private Process StartProcess(SandboxExecutionRequest request, string workspaceDir)
    {
        var command = request.Command ?? request.ToolName;

        if (request.PermissionProfile.AllowedPrograms.Count == 0)
            throw new UnauthorizedAccessException(
                "No allowed programs configured in the permission profile. Sandbox is closed-by-default.");

        if (!request.PermissionProfile.AllowedPrograms.Contains(
                command, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Command '{command}' is not in the allowed programs list");
        }

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

        ConfigureIsolatedEnvironment(psi, request, workspaceDir);

        if (request.ArgumentList is { Count: > 0 })
        {
            foreach (var arg in request.ArgumentList)
                psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    /// <summary>
    /// Returns the first per-request environment grant whose name collides
    /// (case-insensitively) with a reserved variable, or null when all grants are benign.
    /// </summary>
    private static string? FindReservedEnvironmentGrant(SandboxExecutionRequest request)
    {
        if (request.EnvironmentVariables is null)
            return null;

        return request.EnvironmentVariables.Keys.FirstOrDefault(
            name => ReservedEnvironmentVariableNames.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Rejects a request whose environment grants collide with reserved variables — before
    /// any process is spawned — and leaves a signed failure attestation for the audit trail.
    /// Explicit rejection (rather than silently skipping the grant) makes the policy
    /// violation visible to the caller and the audit log.
    /// </summary>
    private async Task<SandboxExecutionResult> RejectReservedGrantAsync(
        SandboxExecutionRequest request, string reservedGrant, CancellationToken ct)
    {
        _logger.LogWarning(
            "Sandbox refused to spawn process for tool {ToolName}: environment grant '{GrantName}' collides with a reserved variable",
            request.ToolName, reservedGrant);

        var errorMessage =
            $"Environment grant rejected: '{reservedGrant}' collides with a reserved variable " +
            "(pinned temp or security-critical) and cannot be overridden by per-request grants.";

        var attestation = await _attestationService.SignAsync(
            Domain.AI.Attestation.AttestationRequest.Failure(request.ToolName, request.Input, errorMessage), ct);

        return new SandboxExecutionResult
        {
            Success = false,
            ErrorMessage = errorMessage,
            Attestation = attestation
        };
    }

    /// <summary>
    /// Rebuilds the child process environment from scratch: only variables named in the
    /// configured allowlist are copied from the host, temp variables are pinned to the
    /// disposable workspace, and pre-validated per-request grants are applied last (grants
    /// colliding with reserved names were already rejected in
    /// <see cref="ExecuteAsync"/>, so they can never un-pin these values).
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
        ProcessStartInfo psi, SandboxExecutionRequest request, string workspaceDir)
    {
        // Closed-by-default: drop everything inherited from the host process.
        psi.EnvironmentVariables.Clear();

        foreach (var name in _sandboxConfig.CurrentValue.ProcessEnvironmentAllowlist)
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

        if (request.EnvironmentVariables is not null)
        {
            foreach (var (name, value) in request.EnvironmentVariables)
                psi.EnvironmentVariables[name] = value;
        }
    }

    private void ApplyResourceLimits(Process process, ResourceLimits limits)
    {
        if (!_resourceLimiter.Apply(process, limits))
        {
            if (!_resourceLimiter.IsSupported)
            {
                KillProcess(process);
                throw new PlatformNotSupportedException(
                    "Process resource limits are not available on this platform. " +
                    "Use container isolation (SandboxIsolationLevel.Container) for cross-platform enforcement.");
            }

            _logger.LogWarning("Failed to apply resource limits to process {ProcessId}", process.Id);
        }
    }

    private void KillProcess(Process process)
    {
        _logger.LogWarning("Process {ProcessId} timed out, killing", process.Id);
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    private async Task<(string stdout, string stderr)> DrainOutputAsync(
        Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            var results = await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            return (results[0], results[1]);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Output drain timed out or failed; returning partial output");
            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
            return (stdout, stderr);
        }
    }

    private async Task<SandboxExecutionResult> BuildTimeoutResultAsync(
        int processId, SandboxExecutionRequest request, TimeSpan elapsed, string? egressDigest, CancellationToken ct)
    {
        var attestation = await SignFailureAsync(
            request.ToolName, request.Input,
            $"Process timed out after {request.Timeout}", egressDigest, ct);

        return new SandboxExecutionResult
        {
            Success = false,
            ErrorMessage = $"Process timed out after {request.Timeout}",
            Attestation = attestation,
            ResourceUsage = BuildUsage(processId, elapsed)
        };
    }

    private async Task<SandboxExecutionResult> BuildCrashResultAsync(
        int processId, int exitCode, string stdout, string stderr,
        SandboxExecutionRequest request, TimeSpan elapsed, string? egressDigest, CancellationToken ct)
    {
        _logger.LogWarning("Process exited with code {ExitCode}: {Stderr}", exitCode, stderr);

        // The crash result carries the stdout produced before the failure, so that output
        // must be bound into the signed attestation — otherwise a stored result's Output
        // could diverge from the attested record without detection.
        var attestation = await _attestationService.SignAsync(
            Domain.AI.Attestation.AttestationRequest.Failure(
                request.ToolName, request.Input,
                $"Process exited with code {exitCode}: {stderr}", output: stdout, egressDigest: egressDigest),
            ct);

        return new SandboxExecutionResult
        {
            Success = false,
            Output = stdout,
            ErrorMessage = stderr,
            ExitCode = exitCode,
            Attestation = attestation,
            ResourceUsage = BuildUsage(processId, elapsed)
        };
    }

    private async Task<SandboxExecutionResult> BuildSuccessResultAsync(
        int processId, string stdout, SandboxExecutionRequest request,
        TimeSpan elapsed, string? egressDigest, CancellationToken ct)
    {
        var attestation = await SignSuccessAsync(
            request.ToolName, request.Input, stdout, egressDigest, ct);

        return new SandboxExecutionResult
        {
            Success = true,
            Output = stdout,
            ExitCode = 0,
            Attestation = attestation,
            ResourceUsage = BuildUsage(processId, elapsed)
        };
    }

    private ResourceUsage BuildUsage(int processId, TimeSpan elapsed)
    {
        var limiterUsage = _resourceLimiter.GetUsage(processId);
        if (limiterUsage is not null)
            return limiterUsage with { WallClockDuration = elapsed };

        return new ResourceUsage { WallClockDuration = elapsed };
    }

    private void CleanupWorkspace(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up sandbox workspace {Path}", path);
        }
    }
}
