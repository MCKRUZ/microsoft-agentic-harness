using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Signs failure attestations for session-start rejections, shared by
/// <see cref="ProcessSandboxSessionFactory"/> and <see cref="DockerSandboxSessionFactory"/> so
/// the "a session has no single <c>Input</c> like a one-shot execution — the resolved command
/// line stands in for it" rationale and payload shape live in one place instead of being
/// copy-pasted per backend.
/// </summary>
public sealed class SandboxSessionRejectionSigner(IAttestationService attestationService)
{
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
                request.ToolName, DescribeCommandLine(request), failureReason, egressDigest: egressDigest),
            ct);

    private static string DescribeCommandLine(SandboxSessionRequest request) =>
        string.Join(' ', new[] { request.Command ?? request.ToolName }.Concat(request.ArgumentList ?? []));
}
