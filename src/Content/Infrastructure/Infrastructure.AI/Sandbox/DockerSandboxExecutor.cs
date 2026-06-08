using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Models.Sandbox;
using Docker.DotNet;
using Docker.DotNet.Models;
using Domain.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Container-based <see cref="ISandboxExecutor"/> implementation using Docker.
/// Provides elevated isolation for tools requiring stronger security boundaries.
/// Enforces the invariant: tools with <c>MinimumIsolation = Container</c> are never
/// downgraded to process isolation when Docker is unavailable.
/// </summary>
public sealed class DockerSandboxExecutor : ISandboxExecutor
{
    private readonly IDockerClient _dockerClient;
    private readonly IAttestationService _attestationService;
    private readonly IOptionsMonitor<SandboxExecutionOptions> _options;
    private readonly ILogger<DockerSandboxExecutor> _logger;
    private readonly ISandboxEgressPreflight? _egressPreflight;

    public DockerSandboxExecutor(
        IDockerClient dockerClient,
        IAttestationService attestationService,
        IOptionsMonitor<SandboxExecutionOptions> options,
        ILogger<DockerSandboxExecutor> logger,
        ISandboxEgressPreflight? egressPreflight = null)
    {
        _dockerClient = dockerClient;
        _attestationService = attestationService;
        _options = options;
        _logger = logger;
        _egressPreflight = egressPreflight;
    }

    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
        var egress = await RunEgressPreflightAsync(request, ct);
        if (egress.Blocked is { } block)
            return block;

        if (!await IsDockerAvailableAsync(ct))
            return await HandleDockerUnavailableAsync(request, ct);

        var workspaceDir = Path.Combine(Path.GetTempPath(), $"docker-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceDir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(workspaceDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string? containerId = null;

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspaceDir, "input.json"), request.Input, ct);

            var image = ResolveImage(request.ToolName);
            await EnsureImageAvailableAsync(image, ct);

            var containerParams = BuildContainerParams(request, workspaceDir, image);
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
            await RemoveContainerSafeAsync(containerId, ct);
            CleanupWorkspace(workspaceDir);
        }
    }

    private async Task<bool> IsDockerAvailableAsync(CancellationToken ct)
    {
        try
        {
            await _dockerClient.System.PingAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker daemon not reachable");
            return false;
        }
    }

    private async Task<SandboxExecutionResult> HandleDockerUnavailableAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
        var isRequired = request.PermissionProfile.MinimumIsolation == SandboxIsolationLevel.Container;

        if (isRequired)
        {
            _logger.LogError(
                "Docker unavailable but tool {ToolName} requires container isolation. Refusing execution",
                request.ToolName);

            var attestation = await _attestationService.SignFailureAsync(
                request.ToolName, request.Input,
                "Container isolation required but Docker is unavailable", ct);

            return new SandboxExecutionResult
            {
                Success = false,
                ErrorMessage = "Container isolation required but Docker is unavailable. Cannot downgrade to process isolation.",
                Attestation = attestation
            };
        }

        _logger.LogWarning("Docker unavailable for tool {ToolName}. Caller may fall back to process isolation", request.ToolName);

        return new SandboxExecutionResult
        {
            Success = false,
            ErrorMessage = "Docker unavailable. Consider fallback to process isolation."
        };
    }

    private CreateContainerParameters BuildContainerParams(
        SandboxExecutionRequest request, string workspaceDir, string image)
    {
        var hasNetworkAccess = request.PermissionProfile.RequiredCapabilities
            .HasFlag(ToolCapability.NetworkAccess);

        List<string>? cmd = null;
        if (request.Command is not null)
        {
            cmd = [request.Command];
            if (request.ArgumentList is { Count: > 0 })
                cmd.AddRange(request.ArgumentList);
        }

        return new CreateContainerParameters
        {
            Image = image,
            Cmd = cmd,
            User = "65534:65534",
            HostConfig = new HostConfig
            {
                Memory = request.Limits.MemoryLimitBytes,
                NetworkMode = hasNetworkAccess ? "bridge" : "none",
                ReadonlyRootfs = true,
                AutoRemove = false,
                Binds = [request.PermissionProfile.RequiredCapabilities.HasFlag(ToolCapability.FileWrite)
                    ? $"{workspaceDir}:/workspace:rw"
                    : $"{workspaceDir}:/workspace:ro"],
                PidsLimit = request.Limits.MaxSubprocesses,
                SecurityOpt = ["no-new-privileges:true"],
                CapDrop = ["ALL"]
            }
        };
    }

    private string ResolveImage(string toolName)
    {
        var options = _options.CurrentValue;

        if (options.ToolOverrides.TryGetValue(toolName, out var toolOverride)
            && !string.IsNullOrEmpty(toolOverride.ContainerImage))
        {
            var overrideImage = toolOverride.ContainerImage;
            ValidateImageAllowed(overrideImage);
            return overrideImage;
        }

        return options.Container.DefaultImage;
    }

    private void ValidateImageAllowed(string image)
    {
        var allowedPrefixes = _options.CurrentValue.Container.AllowedImagePrefixes;
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

    private async Task EnsureImageAvailableAsync(string image, CancellationToken ct)
    {
        try
        {
            await _dockerClient.Images.InspectImageAsync(image, ct);
        }
        catch (DockerImageNotFoundException)
        {
            _logger.LogInformation("Pulling image {Image}", image);
            await _dockerClient.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image },
                null,
                new Progress<JSONMessage>(),
                ct);
        }
    }

    private async Task<SandboxExecutionResult> HandleTimeoutAsync(
        string containerId, SandboxExecutionRequest request, string? egressDigest, CancellationToken ct)
    {
        var gracePeriod = _options.CurrentValue.Container.StopGracePeriodSeconds;
        _logger.LogWarning("Container {ContainerId} timed out, stopping with {GracePeriod}s grace period",
            containerId, gracePeriod);

        try
        {
            await _dockerClient.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = (uint)gracePeriod }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stop container after timeout failed (may already be removed)");
        }

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

        var attestation = await SignFailureAsync(
            request.ToolName, request.Input,
            $"Container exited with code {exitCode}: {logs}", egressDigest, ct);

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
                "Docker sandbox refused to spawn container for tool {ToolName}: egress preflight denied '{Host}'",
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

    private async Task RemoveContainerSafeAsync(string? containerId, CancellationToken ct)
    {
        if (containerId is null)
            return;

        try
        {
            await _dockerClient.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Container removal failed (may already be removed)");
        }
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
            _logger.LogWarning(ex, "Failed to clean up Docker sandbox workspace {Path}", path);
        }
    }
}
