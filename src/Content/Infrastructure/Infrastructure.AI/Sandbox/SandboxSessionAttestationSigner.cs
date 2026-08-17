using System.Text.Json;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Signs attestations for a session's one-time start decision — success or failure — shared by
/// <see cref="ProcessSandboxSessionFactory"/> and <see cref="DockerSandboxSessionFactory"/> so
/// the payload shape and its rationale live in one place instead of being copy-pasted per
/// backend. A session's ongoing conversation is not attested (see <see cref="ISandboxSession"/>'s
/// own remarks on why) — this covers only the moment a session is allowed to begin, the direct
/// counterpart to what the one-shot executors already attest for every terminal outcome.
/// </summary>
public sealed class SandboxSessionAttestationSigner(IAttestationService attestationService)
{
    /// <summary>
    /// Bound on how long <see cref="SignStartAsync"/> waits for the attestation write — mirrors
    /// <c>DockerContainerLaunchPreparer.RemoveContainerSafeAsync</c>'s own reasoning for using a
    /// timeout independent of the caller's token rather than an unbounded wait.
    /// </summary>
    private static readonly TimeSpan StartAttestationWriteTimeout = TimeSpan.FromSeconds(10);


    /// <summary>Signs a failure attestation for <paramref name="reason"/> and wraps it as a failure <see cref="Result{T}"/>.</summary>
    public async Task<Result<ISandboxSession>> RejectAsync(
        SandboxSessionRequest request, string reason, CancellationToken ct, string? egressDigest = null)
    {
        await SignFailureAsync(request, reason, egressDigest, ct);
        return Result<ISandboxSession>.Fail(reason);
    }

    /// <summary>Signs a failure attestation only, for a caller that builds its own <see cref="Result{T}"/> (e.g. to preserve exception context).</summary>
    public Task SignFailureAsync(
        SandboxSessionRequest request, string failureReason, string? egressDigest, CancellationToken ct) =>
        attestationService.SignAsync(
            Domain.AI.Attestation.AttestationRequest.Failure(
                request.ToolName, DescribeSessionInput(request), failureReason, egressDigest: egressDigest),
            ct);

    /// <summary>
    /// Signs a success attestation marking that a session was actually allowed to start and run —
    /// the counterpart to a one-shot executor's <c>SignSuccessAsync</c>. Without this, the audit
    /// trail recorded every session-start <em>refusal</em> but nothing about a session that
    /// actually ran untrusted bundle code, which is the more consequential of the two events, not
    /// the less.
    /// </summary>
    /// <remarks>
    /// Deliberately takes no caller-supplied <see cref="CancellationToken"/>. By the time this
    /// runs, the container is created/started/attached (or the process is spawned) — the
    /// untrusted command is already executing. Signing on the caller's token would let a
    /// caller-side timeout (e.g. <c>McpClient</c>'s <c>InitializationTimeout</c>, itself
    /// bundle-authored) abandon the write for an event that has unconditionally already happened,
    /// leaving the audit trail with neither a start nor a failure record for code that ran on the
    /// host. Bounded by <see cref="StartAttestationWriteTimeout"/> instead, independent of any
    /// caller's token — the same reasoning <c>DockerContainerLaunchPreparer.RemoveContainerSafeAsync</c>
    /// already applies to a different post-hoc cleanup call.
    /// </remarks>
    public async Task SignStartAsync(SandboxSessionRequest request, string? egressDigest)
    {
        // Must await inside this scope, not just return the Task, or `using` would dispose the
        // CancellationTokenSource — and disarm its timeout — before the write actually completes.
        using var cts = new CancellationTokenSource(StartAttestationWriteTimeout);
        await attestationService.SignAsync(
            Domain.AI.Attestation.AttestationRequest.Success(
                request.ToolName, DescribeSessionInput(request), output: "session-started", egressDigest),
            cts.Token);
    }

    /// <summary>
    /// Serializes the fields relevant to a session-start decision as structured JSON rather than
    /// a flattened, space-joined string. A flattened join is ambiguous — <c>["--flag x", "y"]</c>
    /// and <c>["--flag", "x", "y"]</c> produce byte-identical output — and since
    /// <see cref="SandboxSessionRequest.ArgumentList"/> is never shell-interpreted at execution,
    /// a lossy attestation would be the only record an auditor has of what actually ran.
    /// Environment variable NAMES are included (they are what the reserved-name guards act on)
    /// but values are not — a value may itself be a secret, and should not be persisted verbatim
    /// into an audit record.
    /// </summary>
    private static string DescribeSessionInput(SandboxSessionRequest request) => JsonSerializer.Serialize(new
    {
        command = request.Command ?? request.ToolName,
        args = request.ArgumentList ?? [],
        envNames = request.EnvironmentVariables?.Keys.Order().ToArray() ?? [],
        isolation = request.PermissionProfile.MinimumIsolation.ToString(),
        capabilities = request.PermissionProfile.RequiredCapabilities.ToString()
    });
}
