using Application.AI.Common.Interfaces.Escalation;
using Domain.Common.Config;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.AI.Tests.Escalation.Support;

/// <summary>
/// Shared test doubles for the durable governance-state store: an
/// <see cref="IOptionsMonitor{TOptions}"/> over a configurable <see cref="AppConfig"/>, and a
/// deterministic <see cref="Application.AI.Common.Interfaces.Escalation.IGovernanceRecordSealer"/> that needs no HMAC key material.
/// </summary>
internal static class GovernanceStateTestConfig
{
    /// <summary>
    /// Builds an options monitor whose durable-state section carries the given limits.
    /// </summary>
    /// <param name="maxScanRecords">Scan bound for rehydration and reconcile.</param>
    /// <param name="maxPayloadBytes">Per-column serialized payload cap.</param>
    public static IOptionsMonitor<AppConfig> Monitor(
        int maxScanRecords = 10_000,
        int maxPayloadBytes = 1024 * 1024)
    {
        var config = new AppConfig();
        config.AI.Governance.DurableState.MaxScanRecords = maxScanRecords;
        config.AI.Governance.DurableState.MaxPayloadBytes = maxPayloadBytes;

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(config);
        return monitor.Object;
    }
}

/// <summary>
/// Deterministic in-process sealer for tests: signs with a content hash rather than HMAC key
/// material, so the seal still detects payload tampering and cross-record replay (the
/// properties under test) without requiring attestation keys in every fixture.
/// </summary>
/// <remarks>
/// Mirrors the production binding exactly: the signature covers <c>subjectId</c> as well as the
/// payload, so a seal lifted from one record onto another fails here for the same reason it
/// fails against real HMAC keys.
/// </remarks>
internal sealed class FakeGovernanceRecordSealer : IGovernanceRecordSealer
{
    /// <summary>When true, <see cref="VerifyAsync"/> reports failure regardless of the payload.</summary>
    public bool ForceVerificationFailure { get; set; }

    /// <inheritdoc />
    public Task<GovernanceRecordSeal> SealAsync(string subjectId, string payloadJson, CancellationToken ct) =>
        Task.FromResult(new GovernanceRecordSeal(
            Hash(subjectId + "|" + payloadJson),
            "test-key",
            DateTimeOffset.UnixEpoch,
            Hash(subjectId),
            Hash(payloadJson)));

    /// <inheritdoc />
    public Task<bool> VerifyAsync(
        string subjectId, string payloadJson, GovernanceRecordSeal? seal, CancellationToken ct)
    {
        if (ForceVerificationFailure || seal is null)
            return Task.FromResult(false);

        // Both legs must match: the payload hash AND the id-bound signature.
        var payloadMatches = string.Equals(seal.OutputHash, Hash(payloadJson), StringComparison.Ordinal);
        var bindingMatches = string.Equals(
            seal.Signature, Hash(subjectId + "|" + payloadJson), StringComparison.Ordinal);

        return Task.FromResult(payloadMatches && bindingMatches);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));
}
