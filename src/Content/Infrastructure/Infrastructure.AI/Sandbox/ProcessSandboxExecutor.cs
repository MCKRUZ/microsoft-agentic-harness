using System.Diagnostics;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Microsoft.Extensions.Logging;

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

    public ProcessSandboxExecutor(
        IProcessResourceLimiter resourceLimiter,
        IAttestationService attestationService,
        ILogger<ProcessSandboxExecutor> logger,
        TimeProvider timeProvider)
    {
        _resourceLimiter = resourceLimiter;
        _attestationService = attestationService;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    internal Func<string> CreateWorkspaceDirectory { get; set; } = () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    };

    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
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
                return await BuildTimeoutResultAsync(request, elapsed, ct);

            if (process.ExitCode != 0)
                return await BuildCrashResultAsync(process.ExitCode, stdout, stderr, request, elapsed, ct);

            return await BuildSuccessResultAsync(stdout, request, elapsed, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Process sandbox execution failed for tool {ToolName}", request.ToolName);

            var attestation = await _attestationService.SignFailureAsync(
                request.ToolName, request.Input, $"Execution failed: {ex.Message}", ct);

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

    private static Process StartProcess(SandboxExecutionRequest request, string workspaceDir)
    {
        var command = request.Command ?? request.ToolName;

        if (request.PermissionProfile.AllowedPrograms.Count > 0
            && !request.PermissionProfile.AllowedPrograms.Contains(
                command, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Command '{command}' is not in the allowed programs list");
        }

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = request.Arguments ?? string.Empty,
            WorkingDirectory = workspaceDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    private void ApplyResourceLimits(Process process, ResourceLimits limits)
    {
        if (!_resourceLimiter.Apply(process, limits))
            _logger.LogWarning("Failed to apply resource limits to process {ProcessId}", process.Id);
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
        SandboxExecutionRequest request, TimeSpan elapsed, CancellationToken ct)
    {
        var attestation = await _attestationService.SignFailureAsync(
            request.ToolName, request.Input,
            $"Process timed out after {request.Timeout}", ct);

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
        SandboxExecutionRequest request, TimeSpan elapsed, CancellationToken ct)
    {
        _logger.LogWarning("Process exited with code {ExitCode}: {Stderr}", exitCode, stderr);

        var attestation = await _attestationService.SignFailureAsync(
            request.ToolName, request.Input,
            $"Process exited with code {exitCode}: {stderr}", ct);

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
        TimeSpan elapsed, CancellationToken ct)
    {
        var attestation = await _attestationService.SignAsync(
            request.ToolName, request.Input, stdout, ct);

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
