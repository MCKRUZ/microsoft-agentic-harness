using System.Diagnostics;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Starts a <see cref="ProcessSandboxSession"/> — the long-lived, duplex counterpart to
/// <see cref="ProcessSandboxExecutor"/>. Registered via keyed DI on
/// <see cref="SandboxIsolationLevel.Process"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This backend is not a containment boundary.</b> A session started here runs as the same
/// OS user with the same file-system access as the harness process, with no network
/// restriction at all — see the identical caveat on <see cref="ProcessSandboxExecutor"/>'s
/// own environment-isolation remarks. Use <see cref="DockerSandboxSessionFactory"/>
/// (<see cref="SandboxIsolationLevel.Container"/>) for genuine isolation.
/// </para>
/// <para>
/// Because of that, this backend must only ever run a command the operator has explicitly
/// added to <see cref="ToolPermissionProfile.AllowedPrograms"/> — enforced hard by
/// <see cref="ProcessSandboxLaunchPreparer.StartProcess"/>, which this factory does not bypass.
/// A caller must never build a request whose <see cref="SandboxSessionRequest.Command"/> comes
/// from untrusted, caller-supplied content without that allowlist gate standing between them.
/// </para>
/// </remarks>
public sealed class ProcessSandboxSessionFactory(
    ProcessSandboxLaunchPreparer launchPreparer,
    SandboxEgressPreflightRunner egressPreflightRunner,
    SandboxSessionAttestationSigner attestationSigner,
    IOptionsMonitor<SandboxConfig> sandboxConfig,
    ILogger<ProcessSandboxSession> sessionLogger) : ISandboxSessionFactory
{
    /// <inheritdoc />
    public async Task<Result<ISandboxSession>> StartSessionAsync(SandboxSessionRequest request, CancellationToken ct)
    {
        // Matches ProcessSandboxExecutor.ExecuteAsync's equivalent gate: no attestation for this
        // one — it is a host configuration state, not a per-request security decision.
        if (!sandboxConfig.CurrentValue.Enabled)
            return Result<ISandboxSession>.Fail("Sandbox execution is disabled by configuration (Sandbox:Enabled=false).");

        if (ProcessSandboxLaunchPreparer.FindReservedEnvironmentGrant(request.EnvironmentVariables) is { } reservedGrant)
        {
            var reason =
                $"Environment grant rejected: '{reservedGrant}' collides with a reserved variable " +
                "(pinned temp or security-critical) and cannot be overridden by per-request grants.";
            return await attestationSigner.RejectAsync(request, reason, ct);
        }

        var egress = await egressPreflightRunner.EvaluateAsync(request.ToolName, request.EgressPrecheckTargets, ct);
        if (egress.IsDenied)
            return await attestationSigner.RejectAsync(request, egress.ErrorMessage!, ct, egress.Digest);

        return await StartProcessSessionAsync(request, egress.Digest, ct);
    }

    private async Task<Result<ISandboxSession>> StartProcessSessionAsync(
        SandboxSessionRequest request, string? egressDigest, CancellationToken ct)
    {
        var command = request.Command ?? request.ToolName;
        var workspaceDir = launchPreparer.CreateWorkspace();
        Process? process = null;
        ProcessSandboxSession? session = null;

        try
        {
            process = launchPreparer.StartProcess(
                command, request.ArgumentList, request.PermissionProfile, request.EnvironmentVariables, workspaceDir);
            launchPreparer.ApplyResourceLimits(process, request.Limits);

            session = new ProcessSandboxSession(process, launchPreparer, workspaceDir, request.MaxSessionDuration, sessionLogger);

            // Signed only once the session object actually exists — not before it, and not
            // wrapped together with construction in the same try region as a bare `new` — so a
            // failure past this point (this call itself throwing, e.g. on a cancelled ct) can
            // never leave behind a "started" attestation for a session the caller never
            // received. The audit trail must still record that a session actually started, not
            // just that some were refused: a running session is the more consequential event.
            await attestationSigner.SignStartAsync(request, egressDigest, ct);

            return Result<ISandboxSession>.Success(session);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up (e.g. McpConnectionManager's InitializationTimeout) after the
            // process was already spawned. Tear down whatever exists — the constructed session if
            // SignStartAsync is what threw, otherwise the bare process — none of that cleanup
            // depends on ct, so it still runs with ct already cancelled — but skip attestation:
            // signing with a cancelled ct would itself throw, and a caller giving up is not the
            // security-relevant rejection SignFailureAsync exists to record. Then let cancellation
            // propagate.
            await TearDownPartialStartAsync(session, process, workspaceDir);
            throw;
        }
        catch (Exception ex)
        {
            // Single cleanup path for every way starting a session can fail: StartProcess itself
            // throwing after a partial launch, ApplyResourceLimits (which may have already killed
            // the process on a PlatformNotSupportedException path — killing an already-exited
            // process here is a safe no-op), the session constructor throwing (e.g. an invalid
            // MaxSessionDuration — session stays null, so the bare-process branch below applies),
            // or SignStartAsync itself throwing after a valid session already exists (the
            // session's own DisposeAsync then owns tearing the process down).
            await TearDownPartialStartAsync(session, process, workspaceDir);

            var failureReason = $"Process sandbox session failed to start: {ex.Message}";
            await attestationSigner.SignFailureAsync(request, failureReason, egressDigest, ct);
            return Result<ISandboxSession>.Fail(failureReason);
        }
    }

    /// <summary>
    /// Tears down a session start that did not complete. If the <see cref="ProcessSandboxSession"/>
    /// object already exists (the failure happened at or after <c>SignStartAsync</c>), disposing it
    /// is the correct teardown — its own <see cref="ProcessSandboxSession.DisposeAsync"/> kills the
    /// process and cleans up the workspace. Otherwise (the failure happened before the session
    /// object existed) the bare process and workspace are torn down directly.
    /// </summary>
    private async Task TearDownPartialStartAsync(ProcessSandboxSession? session, Process? process, string workspaceDir)
    {
        if (session is not null)
        {
            await session.DisposeAsync();
            return;
        }

        KillAndReleaseIfStarted(process);
        launchPreparer.CleanupWorkspace(workspaceDir);
    }

    /// <summary>
    /// Kills, releases, and disposes a process this factory started but could not hand off to a
    /// session. Delegates the kill itself to <see cref="ProcessSandboxLaunchPreparer.KillProcess"/>
    /// — the same method <see cref="ProcessSandboxSession"/>'s normal teardown path already calls
    /// — rather than a second, silent copy of the same kill-and-swallow logic.
    /// </summary>
    private void KillAndReleaseIfStarted(Process? process)
    {
        if (process is null)
            return;

        launchPreparer.KillProcess(process);
        launchPreparer.ReleaseResourceLimiter(process.Id);
        process.Dispose();
    }
}
