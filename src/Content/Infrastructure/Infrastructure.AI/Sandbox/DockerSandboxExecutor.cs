using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Sandbox;
using Docker.DotNet;
using Docker.DotNet.Models;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Container-based <see cref="ISandboxExecutor"/> implementation using Docker.
/// Provides elevated isolation for tools requiring stronger security boundaries.
/// Enforces the invariant: tools with <c>MinimumIsolation = Container</c> are never
/// downgraded to process isolation when Docker is unavailable.
/// </summary>
/// <remarks>
/// Daemon availability, image resolution/allowlist, container hardening parameters, and
/// workspace lifecycle live in <see cref="DockerContainerLaunchPreparer"/>, shared with
/// <see cref="DockerSandboxSessionFactory"/> so the two never drift from each other's security
/// posture — see #371.
/// </remarks>
public sealed class DockerSandboxExecutor : ISandboxExecutor
{
    private readonly IDockerClient _dockerClient;
    private readonly DockerContainerLaunchPreparer _launchPreparer;
    private readonly IAttestationService _attestationService;
    private readonly IOptionsMonitor<SandboxConfig> _sandboxConfig;
    private readonly ILogger<DockerSandboxExecutor> _logger;
    private readonly SandboxEgressPreflightRunner _egressPreflightRunner;

    public DockerSandboxExecutor(
        IDockerClient dockerClient,
        DockerContainerLaunchPreparer launchPreparer,
        IAttestationService attestationService,
        IOptionsMonitor<SandboxConfig> sandboxConfig,
        ILogger<DockerSandboxExecutor> logger,
        SandboxEgressPreflightRunner egressPreflightRunner)
    {
        _dockerClient = dockerClient;
        _launchPreparer = launchPreparer;
        _attestationService = attestationService;
        _sandboxConfig = sandboxConfig;
        _logger = logger;
        _egressPreflightRunner = egressPreflightRunner;
    }

    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
        if (!_sandboxConfig.CurrentValue.Enabled)
            throw new InvalidOperationException("Sandbox execution is disabled by configuration (Sandbox:Enabled=false).");

        if (!DockerContainerLaunchPreparer.IsValidCpuCoreLimit(request.Limits.CpuCoreLimit))
            return await RejectInvalidCpuLimitAsync(request, ct);

        if (DockerContainerLaunchPreparer.FindReservedEnvironmentGrant(request.EnvironmentVariables) is { } reservedGrant)
            return await RejectReservedEnvironmentGrantAsync(request, reservedGrant, ct);

        // Egress-policy evaluation and the Docker daemon ping are independent I/O with nothing to
        // wait on each other for — run them concurrently rather than paying their sum, matching
        // DockerSandboxSessionFactory's identical preflight (#371).
        var egressTask = RunEgressPreflightAsync(request, ct);
        var dockerAvailableTask = _launchPreparer.IsDockerAvailableAsync(ct);
        await Task.WhenAll(egressTask, dockerAvailableTask);

        var egress = await egressTask;
        if (egress.Blocked is { } block)
            return block;

        if (!await dockerAvailableTask)
            return await HandleDockerUnavailableAsync(request, ct);

        var workspaceDir = _launchPreparer.CreateWorkspace();
        string? containerId = null;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspaceDir, "input.json"), request.Input, ct);

            var image = _launchPreparer.ResolveImage(request.ToolName);
            await _launchPreparer.EnsureImageAvailableAsync(image, ct);

            var containerParams = _launchPreparer.BuildContainerParams(
                request.Command ?? request.ToolName, request.ArgumentList, request.Limits, request.PermissionProfile,
                workspaceDir, image, environmentVariables: request.EnvironmentVariables);
            var createResponse = await _dockerClient.Containers.CreateContainerAsync(containerParams, ct);
            containerId = createResponse.ID;

            await _dockerClient.Containers.StartContainerAsync(containerId, null, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(request.Timeout);

            ContainerWaitResponse waitResponse;
            try
            {
                waitResponse = await _dockerClient.Containers.WaitContainerAsync(containerId, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return await HandleTimeoutAsync(containerId, request, egress.Digest, ct);
            }

            var logs = await GetContainerLogsAsync(containerId, ct);
            var output = await ReadWorkspaceOutputAsync(workspaceDir, ct);

            if (waitResponse.StatusCode != 0)
                return await BuildCrashResultAsync(waitResponse.StatusCode, output, logs, request, egress.Digest, ct);

            return await BuildSuccessResultAsync(output ?? logs, request, egress.Digest, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Docker execution failed for tool {ToolName}", request.ToolName);
            var attestation = await SignFailureAsync(
                request.ToolName, request.Input, $"Docker error: {ex.Message}", egress.Digest, ct);
            return new SandboxExecutionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Attestation = attestation
            };
        }
        finally
        {
            await _launchPreparer.RemoveContainerSafeAsync(containerId);
            _launchPreparer.CleanupWorkspace(workspaceDir);
        }
    }

    /// <summary>
    /// Rejects a request whose <c>CpuCoreLimit</c> is not a positive core count and leaves a
    /// signed failure attestation for the audit trail. Mapping such values to Docker would
    /// produce <c>NanoCPUs = 0</c>, which Docker interprets as unlimited CPU.
    /// </summary>
    private async Task<SandboxExecutionResult> RejectInvalidCpuLimitAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
        _logger.LogWarning(
            "Docker sandbox refused request for tool {ToolName}: CpuCoreLimit {CpuCoreLimit} is not a positive core count",
            request.ToolName, request.Limits.CpuCoreLimit);

        var errorMessage = DockerContainerLaunchPreparer.InvalidCpuCoreLimitMessage(request.Limits.CpuCoreLimit);

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
    /// Rejects a request whose environment grants include a dynamic-linker-hijack variable and
    /// leaves a signed failure attestation for the audit trail — see
    /// <see cref="DockerContainerLaunchPreparer.FindReservedEnvironmentGrant"/> for the threat
    /// this closes.
    /// </summary>
    private async Task<SandboxExecutionResult> RejectReservedEnvironmentGrantAsync(
        SandboxExecutionRequest request, string reservedGrant, CancellationToken ct)
    {
        _logger.LogWarning(
            "Docker sandbox refused request for tool {ToolName}: environment grant '{GrantName}' is a dynamic-linker-hijack vector",
            request.ToolName, reservedGrant);

        var errorMessage = $"Environment grant rejected: '{reservedGrant}' is a dynamic-linker-hijack " +
            "vector and cannot be set for a container-isolated tool.";

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
    /// Refuses execution when the Docker daemon is unavailable.
    /// </summary>
    /// <remarks>
    /// #434: always the hard-refusal, attested outcome. <c>request.PermissionProfile.MinimumIsolation</c>
    /// is deliberately not consulted — every first-party caller already reaches this class through the
    /// <c>Container</c> keyed-DI slot, so container isolation was already required to get here. Note DI
    /// also registers this type unkeyed (needed by the keyed factory lambda itself), so the keyed slot
    /// is convention, not a compile-time guarantee — a consumer wanting a genuine process-isolation
    /// fallback should resolve <see cref="ProcessSandboxExecutor"/> directly.
    /// </remarks>
    private async Task<SandboxExecutionResult> HandleDockerUnavailableAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
        _logger.LogError(
            "Docker unavailable but tool {ToolName} requires container isolation. Refusing execution",
            request.ToolName);

        var attestation = await _attestationService.SignAsync(
            Domain.AI.Attestation.AttestationRequest.Failure(
                request.ToolName, request.Input, DockerUnavailableRefusal.Message),
            ct);

        return new SandboxExecutionResult
        {
            Success = false,
            ErrorMessage = DockerUnavailableRefusal.Message,
            Attestation = attestation
        };
    }

    private async Task<SandboxExecutionResult> HandleTimeoutAsync(
        string containerId, SandboxExecutionRequest request, string? egressDigest, CancellationToken ct)
    {
        await _launchPreparer.StopContainerGracefullyAsync(containerId, ct);

        var attestation = await SignFailureAsync(
            request.ToolName, request.Input,
            $"Container timed out after {request.Timeout}", egressDigest, ct);

        return new SandboxExecutionResult
        {
            Success = false,
            ErrorMessage = $"Container timed out after {request.Timeout}",
            Attestation = attestation
        };
    }

    private async Task<string> GetContainerLogsAsync(string containerId, CancellationToken ct)
    {
        try
        {
            using var logStream = await _dockerClient.Containers.GetContainerLogsAsync(
                containerId,
                false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true },
                ct);

            var (stdout, stderr) = await logStream.ReadOutputToEndAsync(ct);
            return string.IsNullOrEmpty(stdout) ? stderr : stdout;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to retrieve container logs");
            return string.Empty;
        }
    }

    private static async Task<string?> ReadWorkspaceOutputAsync(string workspaceDir, CancellationToken ct)
    {
        var outputPath = Path.Combine(workspaceDir, "output.json");
        if (File.Exists(outputPath))
            return await File.ReadAllTextAsync(outputPath, ct);
        return null;
    }

    private async Task<SandboxExecutionResult> BuildCrashResultAsync(
        long exitCode, string? output, string logs,
        SandboxExecutionRequest request, string? egressDigest, CancellationToken ct)
    {
        _logger.LogWarning("Container exited with code {ExitCode}", exitCode);

        // When the crashed container produced workspace output, that output is returned to
        // the caller and must therefore be bound into the signed attestation. Only when no
        // output exists does the legacy (output-less) failure shape apply.
        var failureReason = $"Container exited with code {exitCode}: {logs}";
        var attestation = output is not null
            ? await _attestationService.SignAsync(
                Domain.AI.Attestation.AttestationRequest.Failure(
                    request.ToolName, request.Input, failureReason, output: output, egressDigest: egressDigest),
                ct)
            : await SignFailureAsync(
                request.ToolName, request.Input, failureReason, egressDigest, ct);

        return new SandboxExecutionResult
        {
            Success = false,
            Output = output,
            ErrorMessage = logs,
            ExitCode = (int)exitCode,
            Attestation = attestation
        };
    }

    private async Task<SandboxExecutionResult> BuildSuccessResultAsync(
        string output, SandboxExecutionRequest request, string? egressDigest, CancellationToken ct)
    {
        var attestation = await SignSuccessAsync(
            request.ToolName, request.Input, output, egressDigest, ct);

        return new SandboxExecutionResult
        {
            Success = true,
            Output = output,
            ExitCode = 0,
            Attestation = attestation
        };
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
}
