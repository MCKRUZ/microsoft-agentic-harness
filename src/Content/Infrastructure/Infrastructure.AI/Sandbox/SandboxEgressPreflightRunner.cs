using Application.AI.Common.Interfaces.Sandbox;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Outcome of a <see cref="SandboxEgressPreflightRunner"/> evaluation. <see cref="Digest"/> is
/// populated whenever the preflight actually ran (allowed or denied) so callers can bind it into
/// a signed attestation; it is null only when there was nothing to evaluate.
/// </summary>
public sealed record SandboxEgressPreflightOutcome(
    bool IsDenied, string? Digest, string? FailureReason, string? ErrorMessage)
{
    public static SandboxEgressPreflightOutcome Allowed(string? digest) => new(false, digest, null, null);

    public static SandboxEgressPreflightOutcome Denied(string? digest, string failureReason, string errorMessage) =>
        new(true, digest, failureReason, errorMessage);
}

/// <summary>
/// Runs the egress preflight gate shared by both one-shot sandbox execution
/// (<see cref="ProcessSandboxExecutor"/>, <see cref="DockerSandboxExecutor"/>) and long-lived
/// sandbox sessions (<see cref="ProcessSandboxSessionFactory"/>, <see cref="DockerSandboxSessionFactory"/>).
/// Extracted so a session cannot skip the same "cannot bypass policy" check a one-shot execution
/// enforces — see #371's residual-risk note: this preflight evaluates only the URIs a caller
/// <em>declares</em> up front, it does not constrain sockets opened after the sandbox starts.
/// </summary>
public sealed class SandboxEgressPreflightRunner(
    ISandboxEgressPreflight? egressPreflight,
    ILogger<SandboxEgressPreflightRunner> logger)
{
    public async Task<SandboxEgressPreflightOutcome> EvaluateAsync(
        string toolName, IReadOnlyList<Uri>? targets, CancellationToken ct)
    {
        if (egressPreflight is null || targets is not { Count: > 0 })
            return SandboxEgressPreflightOutcome.Allowed(null);

        var decisions = await egressPreflight.EvaluateAsync(targets, ct);
        var digest = egressPreflight.ComputeDigest(decisions);

        var denied = decisions.FirstOrDefault(d => !d.Allowed);
        if (denied is null)
            return SandboxEgressPreflightOutcome.Allowed(digest);

        logger.LogWarning(
            "Sandbox refused to start for tool {ToolName}: egress preflight denied '{Host}'",
            toolName, denied.Target.Host);

        return SandboxEgressPreflightOutcome.Denied(
            digest,
            $"Egress preflight denied: {denied.Target} ({denied.Reason})",
            $"Egress preflight denied: {denied.Target}");
    }
}
