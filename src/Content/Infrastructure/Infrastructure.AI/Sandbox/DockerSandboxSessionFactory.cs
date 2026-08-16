using Application.AI.Common.Interfaces.Sandbox;
using Docker.DotNet;
using Docker.DotNet.Models;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Starts a <see cref="DockerSandboxSession"/> — the long-lived, duplex counterpart to
/// <see cref="DockerSandboxExecutor"/>. Registered via keyed DI on
/// <see cref="SandboxIsolationLevel.Container"/>. This is the genuine isolation boundary among
/// the two session backends — see the caveats on <see cref="ProcessSandboxSessionFactory"/>.
/// </summary>
public sealed class DockerSandboxSessionFactory(
    IDockerClient dockerClient,
    DockerContainerLaunchPreparer launchPreparer,
    SandboxEgressPreflightRunner egressPreflightRunner,
    SandboxSessionRejectionSigner rejectionSigner,
    IOptionsMonitor<SandboxConfig> sandboxConfig,
    ILogger<DockerSandboxSession> sessionLogger) : ISandboxSessionFactory
{
    /// <inheritdoc />
    public async Task<Result<ISandboxSession>> StartSessionAsync(SandboxSessionRequest request, CancellationToken ct)
    {
        // Matches DockerSandboxExecutor.ExecuteAsync's equivalent gate: no attestation for this
        // one — it is a host configuration state, not a per-request security decision.
        if (!sandboxConfig.CurrentValue.Enabled)
            return Result<ISandboxSession>.Fail("Sandbox execution is disabled by configuration (Sandbox:Enabled=false).");

        if (!DockerContainerLaunchPreparer.IsValidCpuCoreLimit(request.Limits.CpuCoreLimit))
        {
            var reason = DockerContainerLaunchPreparer.InvalidCpuCoreLimitMessage(request.Limits.CpuCoreLimit);
            return await rejectionSigner.RejectAsync(request, reason, ct);
        }

        if (DockerContainerLaunchPreparer.FindReservedEnvironmentGrant(request.EnvironmentVariables) is { } reservedGrant)
        {
            var reason = $"Environment grant rejected: '{reservedGrant}' is a dynamic-linker-hijack " +
                "vector and cannot be set for a container-isolated tool.";
            return await rejectionSigner.RejectAsync(request, reason, ct);
        }

        // Egress-policy evaluation and the Docker daemon ping are independent I/O with nothing to
        // wait on each other for — run them concurrently rather than paying their sum.
        var egressTask = egressPreflightRunner.EvaluateAsync(request.ToolName, request.EgressPrecheckTargets, ct);
        var dockerAvailableTask = launchPreparer.IsDockerAvailableAsync(ct);
        await Task.WhenAll(egressTask, dockerAvailableTask);

        var egress = await egressTask;
        if (egress.IsDenied)
            return await rejectionSigner.RejectAsync(request, egress.ErrorMessage!, ct, egress.Digest);

        if (!await dockerAvailableTask)
            return await HandleDockerUnavailableAsync(request, egress.Digest, ct);

        return await StartContainerSessionAsync(request, egress.Digest, ct);
    }

    private async Task<Result<ISandboxSession>> HandleDockerUnavailableAsync(
        SandboxSessionRequest request, string? egressDigest, CancellationToken ct)
    {
        var isRequired = request.PermissionProfile.MinimumIsolation == SandboxIsolationLevel.Container;
        if (isRequired)
        {
            // Matches DockerSandboxExecutor: only the "required" branch is attested — the softer
            // fallback-suggestion branch below is a hint to the caller, not a security refusal.
            return await rejectionSigner.RejectAsync(
                request,
                "Container isolation required but Docker is unavailable. Cannot downgrade to process isolation.",
                ct, egressDigest);
        }

        return Result<ISandboxSession>.Fail("Docker unavailable. Consider fallback to process isolation.");
    }

    private async Task<Result<ISandboxSession>> StartContainerSessionAsync(
        SandboxSessionRequest request, string? egressDigest, CancellationToken ct)
    {
        var workspaceDir = launchPreparer.CreateWorkspace();
        string? containerId = null;

        try
        {
            var image = launchPreparer.ResolveImage(request.ToolName);
            await launchPreparer.EnsureImageAvailableAsync(image, ct);

            var containerParams = launchPreparer.BuildContainerParams(
                request.Command ?? request.ToolName, request.ArgumentList, request.Limits, request.PermissionProfile,
                workspaceDir, image, interactive: true, environmentVariables: request.EnvironmentVariables);
            var createResponse = await dockerClient.Containers.CreateContainerAsync(containerParams, ct);
            containerId = createResponse.ID;

            await dockerClient.Containers.StartContainerAsync(containerId, null, ct);

            var attachStream = await dockerClient.Containers.AttachContainerAsync(
                containerId,
                tty: false,
                new ContainerAttachParameters { Stream = true, Stdin = true, Stdout = true, Stderr = true },
                ct);

            return Result<ISandboxSession>.Success(new DockerSandboxSession(
                dockerClient, launchPreparer, attachStream, containerId, request.ToolName, workspaceDir,
                request.MaxSessionDuration, sessionLogger));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await launchPreparer.RemoveContainerSafeAsync(containerId);
            launchPreparer.CleanupWorkspace(workspaceDir);

            var failureReason = $"Docker error: {ex.Message}";
            await rejectionSigner.SignFailureAsync(request, failureReason, egressDigest, ct);
            return Result<ISandboxSession>.Fail(failureReason);
        }
    }
}
