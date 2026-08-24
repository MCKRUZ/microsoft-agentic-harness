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
    /// <summary>
    /// Stable, scrubbed caller-visible failure code for an unexpected Docker error — the raw
    /// exception message (which can embed the workspace host path, image name, or daemon socket
    /// path) is logged via structured logging instead of returned to the caller in
    /// <see cref="Result{T}"/>, matching this repo's <c>skill_training.*</c> stable-code
    /// convention. The full diagnostic detail is still attested (an internal audit record, not a
    /// caller-facing string) via <see cref="SandboxSessionAttestationSigner.SignFailureAsync"/>.
    /// </summary>
    private const string DockerErrorFailureCode = "sandbox.docker_session_start_failed";

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

    /// <summary>
    /// Refuses session start when the Docker daemon is unavailable.
    /// </summary>
    /// <remarks>
    /// #434: always the hard-refusal, attested outcome — matches
    /// <see cref="DockerSandboxExecutor"/>'s identical fix, including that fix's remarks on the
    /// unkeyed <c>AddScoped</c> registration that also exists in DI alongside the keyed slot every
    /// first-party caller actually uses.
    /// </remarks>
    private async Task<Result<ISandboxSession>> HandleDockerUnavailableAsync(
        SandboxSessionRequest request, string? egressDigest, CancellationToken ct)
        => await attestationSigner.RejectAsync(
            request,
            "Container isolation required but Docker is unavailable. Cannot downgrade to process isolation.",
            ct, egressDigest);

    private async Task<Result<ISandboxSession>> StartContainerSessionAsync(
        SandboxSessionRequest request, string? egressDigest, CancellationToken ct)
    {
        var workspaceDir = launchPreparer.CreateWorkspace();
        string? containerId = null;
        DockerSandboxSession? session = null;
        MultiplexedStream? attachStream = null;
        // Populated once ResolveImage runs below; stays null for a failure ahead of that point, so
        // the attestation signer's own fallback to request.ContainerImage still applies to those.
        string? resolvedImage = null;

        try
        {
            // Seeding (blocking disk I/O, potentially a whole bundle's worth of files) and the
            // image pull (Docker daemon I/O) have no data dependency on each other — both must
            // finish before the container starts, but neither needs the other's result first, so
            // they run concurrently rather than paying their sum. Same shape as the egress-check +
            // Docker-ping pairing above in StartSessionAsync. SeedWorkspace itself stays the
            // preparer's own single call — not two direct SandboxWorkspace calls at this site — so
            // ownership of "how a Docker-tier workspace gets initialized" stays with the one class
            // already responsible for CreateWorkspace/CleanupWorkspace; Task.Run only moves WHEN
            // that call happens, not who owns it.
            resolvedImage = launchPreparer.ResolveImage(request.ToolName, request.ContainerImage);
            var seedTask = request.WorkspaceSeedDirectory is { } seedDirectory
                ? Task.Run(() => launchPreparer.SeedWorkspace(workspaceDir, seedDirectory), ct)
                : Task.CompletedTask;
            var imagePullTask = launchPreparer.EnsureImageAvailableAsync(resolvedImage, ct);
            await Task.WhenAll(seedTask, imagePullTask);

            var containerParams = launchPreparer.BuildContainerParams(
                request.Command ?? request.ToolName, request.ArgumentList, request.Limits, request.PermissionProfile,
                workspaceDir, resolvedImage, interactive: true, environmentVariables: request.EnvironmentVariables,
                workingDirectory: request.WorkspaceSeedDirectory is not null ? "/workspace" : null);
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
            // (e.g. the attestation write itself timing out) can never leave behind a "started"
            // attestation for a session the caller never received. The audit trail must still
            // record that a session actually started, not just that some were refused: a running
            // session is the more consequential event. Deliberately not passed ct — see
            // SignStartAsync's own remarks for why signing this specific event must not be
            // abandonable by the caller's token.
            await attestationSigner.SignStartAsync(request, egressDigest, resolvedImage);

            return Result<ISandboxSession>.Success(session);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Filtered on ct specifically: an OperationCanceledException whose token is NOT ct
            // (e.g. Docker.DotNet's own internal HTTP timeout, which does not use the caller's
            // token) is not "the caller gave up" and must fall into the catch (Exception) below
            // instead, where it gets a proper attested Result.Fail rather than an unattested
            // rethrow.
            //
            // Here, the caller genuinely did give up (e.g. McpConnectionManager's
            // InitializationTimeout) after the container was already created/started. Tear down
            // whatever exists — the constructed session if SignStartAsync is what threw,
            // otherwise the bare container — none of that cleanup depends on ct, so it still runs
            // with ct already cancelled — but skip attestation here: SignFailureAsync's ct would
            // already be cancelled too, and a caller giving up before this point is not the
            // security-relevant rejection it exists to record. Then let cancellation propagate as
            // the caller expects.
            await TearDownPartialStartAsync(session, attachStream, containerId, workspaceDir);
            throw;
        }
        catch (InvalidOperationException ex)
        {
            // A deliberate, safe rejection message from DockerContainerLaunchPreparer's own image
            // allowlist check ("Image 'x' not in allowed registry list...") — not raw external
            // exception text, so unlike catch (Exception) below it is not scrubbed: there is
            // nothing here an operator shouldn't see, and hiding it would make a config mistake
            // harder to diagnose for no security benefit.
            await TearDownPartialStartAsync(session, attachStream, containerId, workspaceDir);

            await attestationSigner.SignFailureAsync(request, ex.Message, egressDigest, ct, resolvedImage);
            return Result<ISandboxSession>.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            // Unlike the InvalidOperationException arm above, ex.Message here can be raw Docker
            // daemon exception text (workspace host path, image name, daemon socket path) —
            // scrubbed from the caller-visible result; the full detail is still logged and
            // attested.
            await TearDownPartialStartAsync(session, attachStream, containerId, workspaceDir);

            sessionLogger.LogWarning(ex, "Docker sandbox session failed to start for tool {ToolName}", request.ToolName);
            await attestationSigner.SignFailureAsync(request, $"Docker error: {ex.Message}", egressDigest, ct, resolvedImage);
            return Result<ISandboxSession>.Fail(DockerErrorFailureCode);
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
