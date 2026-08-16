using System.Diagnostics;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Sandbox;
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
/// <remarks>
/// Program allowlist enforcement, environment isolation, workspace lifecycle, and resource-limit
/// application live in <see cref="ProcessSandboxLaunchPreparer"/>, shared with
/// <see cref="ProcessSandboxSessionFactory"/> so the two never drift from each other's security
/// posture — see #371.
/// </remarks>
public sealed class ProcessSandboxExecutor : ISandboxExecutor
{
    private readonly ProcessSandboxLaunchPreparer _launchPreparer;
    private readonly IAttestationService _attestationService;
    private readonly ILogger<ProcessSandboxExecutor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<SandboxConfig> _sandboxConfig;
    private readonly SandboxEgressPreflightRunner _egressPreflightRunner;

    public ProcessSandboxExecutor(
        ProcessSandboxLaunchPreparer launchPreparer,
        IAttestationService attestationService,
        ILogger<ProcessSandboxExecutor> logger,
        TimeProvider timeProvider,
        IOptionsMonitor<SandboxConfig> sandboxConfig,
        SandboxEgressPreflightRunner egressPreflightRunner)
    {
        _launchPreparer = launchPreparer;
        _attestationService = attestationService;
        _logger = logger;
        _timeProvider = timeProvider;
        _sandboxConfig = sandboxConfig;
        _egressPreflightRunner = egressPreflightRunner;
    }

    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
        if (!_sandboxConfig.CurrentValue.Enabled)
            throw new InvalidOperationException("Sandbox execution is disabled by configuration (Sandbox:Enabled=false).");

        if (ProcessSandboxLaunchPreparer.FindReservedEnvironmentGrant(request.EnvironmentVariables) is { } reservedGrant)
            return await RejectReservedGrantAsync(request, reservedGrant, ct);

        var egress = await RunEgressPreflightAsync(request, ct);
        if (egress.Blocked is { } block)
            return block;

        var workspaceDir = _launchPreparer.CreateWorkspace();
        var startTimestamp = _timeProvider.GetTimestamp();
        int? limitedProcessId = null;

        try
        {
            using var process = StartProcess(request, workspaceDir);
            _launchPreparer.ApplyResourceLimits(process, request.Limits);
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
                _launchPreparer.KillProcess(process);
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
                _launchPreparer.ReleaseResourceLimiter(pid);

            _launchPreparer.CleanupWorkspace(workspaceDir);
        }
    }

    private async Task<(SandboxExecutionResult? Blocked, string? Digest)> RunEgressPreflightAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
        var outcome = await _egressPreflightRunner.EvaluateAsync(request.ToolName, request.EgressPrecheckTargets, ct);
        if (!outcome.IsDenied)
            return (null, outcome.Digest);

        var attestation = await _attestationService.SignAsync(
            Domain.AI.Attestation.AttestationRequest.Failure(
                request.ToolName, request.Input, outcome.FailureReason!, egressDigest: outcome.Digest),
            ct);

        return (new SandboxExecutionResult
        {
            Success = false,
            ErrorMessage = outcome.ErrorMessage,
            Attestation = attestation
        }, outcome.Digest);
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

    private Process StartProcess(SandboxExecutionRequest request, string workspaceDir) =>
        _launchPreparer.StartProcess(
            request.Command ?? request.ToolName,
            request.ArgumentList,
            request.PermissionProfile,
            request.EnvironmentVariables,
            workspaceDir);

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
            ResourceUsage = _launchPreparer.BuildUsage(processId, elapsed)
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
            ResourceUsage = _launchPreparer.BuildUsage(processId, elapsed)
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
            ResourceUsage = _launchPreparer.BuildUsage(processId, elapsed)
        };
    }
}
