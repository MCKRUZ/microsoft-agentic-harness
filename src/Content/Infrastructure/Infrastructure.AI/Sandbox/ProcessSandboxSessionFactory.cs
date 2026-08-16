using System.Diagnostics;
using Application.AI.Common.Interfaces.Attestation;
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
    IAttestationService attestationService,
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
            return await RejectAsync(request, reason, ct);
        }

        var egress = await egressPreflightRunner.EvaluateAsync(request.ToolName, request.EgressPrecheckTargets, ct);
        if (egress.IsDenied)
            return await RejectAsync(request, egress.ErrorMessage!, ct, egress.Digest);

        return await StartProcessSessionAsync(request, egress.Digest, ct);
    }

    private async Task<Result<ISandboxSession>> StartProcessSessionAsync(
        SandboxSessionRequest request, string? egressDigest, CancellationToken ct)
    {
        var command = request.Command ?? request.ToolName;
        var workspaceDir = launchPreparer.CreateWorkspace();
        Process? process = null;

        try
        {
            process = launchPreparer.StartProcess(
                command, request.ArgumentList, request.PermissionProfile, request.EnvironmentVariables, workspaceDir);
            launchPreparer.ApplyResourceLimits(process, request.Limits);

            return Result<ISandboxSession>.Success(
                new ProcessSandboxSession(process, launchPreparer, workspaceDir, request.MaxSessionDuration, sessionLogger));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Single cleanup path for every way starting a session can fail once the process
            // exists: StartProcess itself throwing after a partial launch, ApplyResourceLimits
            // (which may have already killed the process on a PlatformNotSupportedException path
            // — killing an already-exited process here is a safe no-op), or the session
            // constructor throwing (e.g. an invalid MaxSessionDuration).
            KillAndReleaseIfStarted(process);
            launchPreparer.CleanupWorkspace(workspaceDir);

            await SignFailureAsync(request, $"Process sandbox session failed to start: {ex.Message}", egressDigest, ct);
            return Result<ISandboxSession>.Fail(ex.Message);
        }
    }

    private void KillAndReleaseIfStarted(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }

        launchPreparer.ReleaseResourceLimiter(process.Id);
        process.Dispose();
    }

    private async Task<Result<ISandboxSession>> RejectAsync(
        SandboxSessionRequest request, string reason, CancellationToken ct, string? egressDigest = null)
    {
        await SignFailureAsync(request, reason, egressDigest, ct);
        return Result<ISandboxSession>.Fail(reason);
    }

    /// <summary>
    /// Signs a failure attestation for a session-start rejection. A session has no single
    /// <c>Input</c> the way a one-shot execution does, so the resolved command line — what was
    /// about to run — stands in as the attested "input" for this one-time start decision.
    /// </summary>
    private Task SignFailureAsync(
        SandboxSessionRequest request, string failureReason, string? egressDigest, CancellationToken ct) =>
        attestationService.SignAsync(
            Domain.AI.Attestation.AttestationRequest.Failure(
                request.ToolName, DescribeCommandLine(request), failureReason, egressDigest: egressDigest),
            ct);

    private static string DescribeCommandLine(SandboxSessionRequest request) =>
        string.Join(' ', new[] { request.Command ?? request.ToolName }.Concat(request.ArgumentList ?? []));
}
