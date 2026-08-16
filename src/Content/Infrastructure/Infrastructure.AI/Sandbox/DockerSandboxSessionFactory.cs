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
    SandboxSessionAttestationSigner attestationSigner,
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
            return await attestationSigner.RejectAsync(request, reason, ct);
        }

        if (DockerContainerLaunchPreparer.FindReservedEnvironmentGrant(request.EnvironmentVariables) is { } reservedGrant)
        {
            var reason = $"Environment grant rejected: '{reservedGrant}' is a dynamic-linker-hijack " +
                "vector and cannot be set for a container-isolated tool.";
            return await attestationSigner.RejectAsync(request, reason, ct);
        }

        // Egress-policy evaluation and the Docker daemon ping are independent I/O with nothing to
        // wait on each other for — run them concurrently rather than paying their sum.
        var egressTask = egressPreflightRunner.EvaluateAsync(request.ToolName, request.EgressPrecheckTargets, ct);
        var dockerAvailableTask = launchPreparer.IsDockerAvailableAsync(ct);
        await Task.WhenAll(egressTask, dockerAvailableTask);

        var egress = await egressTask;
        if (egress.IsDenied)
            return await attestationSigner.RejectAsync(request, egress.ErrorMessage!, ct, egress.Digest);

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
            return await attestationSigner.RejectAsync(
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
        DockerSandboxSession? session = null;
        MultiplexedStream? attachStream = null;

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

            attachStream = await dockerClient.Containers.AttachContainerAsync(
                containerId,
                tty: false,
                new ContainerAttachParameters { Stream = true, Stdin = true, Stdout = true, Stderr = true },
                ct);

            // Constructed before signing, not after: the session takes ownership of attachStream
            // (its own DisposeAsync disposes it) the moment it exists, so a failure past this
            // point — including SignStartAsync itself throwing — cannot leak the live attach
            // connection the way returning it bare would.
            session = new DockerSandboxSession(
                launchPreparer, attachStream, containerId, request.ToolName, workspaceDir,
                request.MaxSessionDuration, sessionLogger);

            // Signed only once the session object actually exists, so a failure past this point
            // can never leave behind a "started" attestation for a session the caller never
            // received. The audit trail must still record that a session actually started, not
            // just that some were refused: a running session is the more consequential event.
            await attestationSigner.SignStartAsync(request, egressDigest, ct);

            return Result<ISandboxSession>.Success(session);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up (e.g. McpConnectionManager's InitializationTimeout) after the
            // container was already created/started. Tear down whatever exists — the constructed
            // session if SignStartAsync is what threw, otherwise the bare container — none of
            // that cleanup depends on ct, so it still runs with ct already cancelled — but skip
            // attestation: signing with a cancelled ct would itself throw, and a caller giving up
            // is not the security-relevant rejection SignFailureAsync exists to record. Then let
            // cancellation propagate as the caller expects.
            await TearDownPartialStartAsync(session, attachStream, containerId, workspaceDir);
            throw;
        }
        catch (Exception ex)
        {
            await TearDownPartialStartAsync(session, attachStream, containerId, workspaceDir);

            var failureReason = $"Docker error: {ex.Message}";
            await attestationSigner.SignFailureAsync(request, failureReason, egressDigest, ct);
            return Result<ISandboxSession>.Fail(failureReason);
        }
    }

    /// <summary>
    /// Tears down a session start that did not complete. If the <see cref="DockerSandboxSession"/>
    /// object already exists (the failure happened at or after <c>SignStartAsync</c>), disposing
    /// it is the correct teardown — its own <see cref="DockerSandboxSession.DisposeAsync"/> stops
    /// and removes the container, disposes the attach stream, and cleans up the workspace.
    /// Otherwise the bare container and workspace are torn down directly, and
    /// <paramref name="attachStream"/> — if the failure happened inside the session constructor
    /// itself, after the attach succeeded but before any object took ownership of it — is disposed
    /// directly too, so a construction failure (e.g. an invalid <c>MaxSessionDuration</c>) cannot
    /// leak the live Docker attach connection.
    /// </summary>
    private async Task TearDownPartialStartAsync(
        DockerSandboxSession? session, MultiplexedStream? attachStream, string? containerId, string workspaceDir)
    {
        if (session is not null)
        {
            await session.DisposeAsync();
            return;
        }

        attachStream?.Dispose();
        await launchPreparer.RemoveContainerSafeAsync(containerId);
        launchPreparer.CleanupWorkspace(workspaceDir);
    }
}
