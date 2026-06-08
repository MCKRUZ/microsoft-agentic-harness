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

    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
        if (!_sandboxConfig.CurrentValue.Enabled)
            throw new InvalidOperationException("Sandbox execution is disabled by configuration (Sandbox:Enabled=false).");

        var egress = await RunEgressPreflightAsync(request, ct);
        if (egress.Blocked is { } block)
            return block;

        var workspaceDir = CreateWorkspaceDirectory();
        var startTimestamp = _timeProvider.GetTimestamp();

        try
        {
            using var process = StartProcess(request, workspaceDir);
            ApplyResourceLimits(process, request.Limits);

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
                return await BuildTimeoutResultAsync(request, elapsed, egress.Digest, ct);

            if (process.ExitCode != 0)
                return await BuildCrashResultAsync(process.ExitCode, stdout, stderr, request, elapsed, egress.Digest, ct);

            return await BuildSuccessResultAsync(stdout, request, elapsed, egress.Digest, ct);
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

            var attestation = await _attestationService.SignFailureWithEgressAsync(
                request.ToolName,
                request.Input,
                $"Egress preflight denied: {denied.Target} ({denied.Reason})",
                digest,
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
    {
        return egressDigest is null
            ? _attestationService.SignFailureAsync(toolName, input, failureReason, ct)
            : _attestationService.SignFailureWithEgressAsync(toolName, input, failureReason, egressDigest, ct);
    }

    private Task<Domain.AI.Attestation.ToolExecutionAttestation> SignSuccessAsync(
        string toolName, string input, string output, string? egressDigest, CancellationToken ct)
    {
        return egressDigest is null
            ? _attestationService.SignAsync(toolName, input, output, ct)
            : _attestationService.SignWithEgressAsync(toolName, input, output, egressDigest, ct);
    }

    private static Process StartProcess(SandboxExecutionRequest request, string workspaceDir)
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

        if (request.ArgumentList is { Count: > 0 })
        {
            foreach (var arg in request.ArgumentList)
                psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
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
        SandboxExecutionRequest request, TimeSpan elapsed, string? egressDigest, CancellationToken ct)
    {
        var attestation = await SignFailureAsync(
            request.ToolName, request.Input,
            $"Process timed out after {request.Timeout}", egressDigest, ct);

        return new SandboxExecutionResult
        {
            Success = false,
            ErrorMessage = $"Process timed out after {request.Timeout}",
            Attestation = attestation,
            ResourceUsage = BuildUsage(elapsed)
        };
    }

    private async Task<SandboxExecutionResult> BuildCrashResultAsync(
        int exitCode, string stdout, string stderr,
        SandboxExecutionRequest request, TimeSpan elapsed, string? egressDigest, CancellationToken ct)
    {
        _logger.LogWarning("Process exited with code {ExitCode}: {Stderr}", exitCode, stderr);

        var attestation = await SignFailureAsync(
            request.ToolName, request.Input,
            $"Process exited with code {exitCode}: {stderr}", egressDigest, ct);

        return new SandboxExecutionResult
        {
            Success = false,
            Output = stdout,
            ErrorMessage = stderr,
            ExitCode = exitCode,
            Attestation = attestation,
            ResourceUsage = BuildUsage(elapsed)
        };
    }

    private async Task<SandboxExecutionResult> BuildSuccessResultAsync(
        string stdout, SandboxExecutionRequest request,
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
            ResourceUsage = BuildUsage(elapsed)
        };
    }

    private ResourceUsage BuildUsage(TimeSpan elapsed)
    {
        var limiterUsage = _resourceLimiter.GetUsage();
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
