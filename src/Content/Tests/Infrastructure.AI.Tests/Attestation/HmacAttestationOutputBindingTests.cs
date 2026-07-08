using System.Security.Cryptography;
using System.Text;
using Domain.AI.Attestation;
using FluentAssertions;
using Infrastructure.AI.Attestation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Attestation;

/// <summary>
/// Security regression coverage for sandbox audit finding A6-2: attestations were not bound
/// to the actual produced output. <c>VerifyAsync</c> validates only the attestation's own
/// fields, so a <c>SandboxExecutionResult.Output</c> tampered after signing still "verified".
/// Crash results carried output that the failure attestation never covered at all.
/// These tests pin the output-bound verification path (<c>VerifyBoundAsync</c>) and the
/// failure-with-output signing shape (<c>SignFailureWithOutputAsync</c>).
/// </summary>
public sealed class HmacAttestationOutputBindingTests
{
    private static readonly string TestKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _timeProvider = new(FixedTime);

    private HmacAttestationService CreateService()
    {
        var options = new AttestationKeyOptions
        {
            CurrentKeyVersion = "v1",
            HmacKeys = [new HmacKeyEntry { Version = "v1", Key = TestKeyBase64 }]
        };

        var monitor = Mock.Of<IOptionsMonitor<AttestationKeyOptions>>(
            m => m.CurrentValue == options);

        return new HmacAttestationService(
            monitor,
            _timeProvider,
            NullLogger<HmacAttestationService>.Instance);
    }

    [Fact]
    public async Task VerifyBound_ActualOutputMatches_ReturnsTrue()
    {
        var service = CreateService();
        var attestation = await service.SignAsync("calculator", "{\"a\":1}", "{\"result\":2}", CancellationToken.None);

        var result = await service.VerifyBoundAsync(attestation, "{\"result\":2}", CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyBound_ActualOutputDiverges_ReturnsFalse()
    {
        var service = CreateService();
        var attestation = await service.SignAsync("calculator", "{\"a\":1}", "{\"result\":2}", CancellationToken.None);

        // The gap this closes: signature-only verification cannot see the real output,
        // so a tampered stored output still passes VerifyAsync.
        (await service.VerifyAsync(attestation, CancellationToken.None)).Should().BeTrue();

        var bound = await service.VerifyBoundAsync(attestation, "{\"result\":999}", CancellationToken.None);

        bound.Should().BeFalse(
            "an attestation must only verify against the exact output bytes that were signed");
    }

    [Fact]
    public async Task VerifyBound_NoOutputHashOnAttestation_ReturnsFalse()
    {
        var service = CreateService();
        var attestation = await service.SignFailureAsync("calculator", "{\"a\":1}", "boom", CancellationToken.None);

        var result = await service.VerifyBoundAsync(attestation, "any-output", CancellationToken.None);

        result.Should().BeFalse("an attestation that recorded no output cannot vouch for any output");
    }

    [Fact]
    public async Task SignFailureWithOutput_BindsContentHashOfProducedOutput()
    {
        var service = CreateService();

        var attestation = await service.SignFailureWithOutputAsync(
            "compiler", "{\"src\":\"x\"}", "exit code 1", "partial diagnostics", egressDigest: null, CancellationToken.None);

        attestation.IsFailureAttestation.Should().BeTrue();
        attestation.FailureReason.Should().Be("exit code 1");
        attestation.OutputHash.Should().Be(Sha256Hex("partial diagnostics"));
        (await service.VerifyAsync(attestation, CancellationToken.None)).Should().BeTrue();
        (await service.VerifyBoundAsync(attestation, "partial diagnostics", CancellationToken.None)).Should().BeTrue();
        (await service.VerifyBoundAsync(attestation, "tampered diagnostics", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SignFailureWithOutput_TamperedOutputHash_FailsSignatureVerification()
    {
        var service = CreateService();
        var attestation = await service.SignFailureWithOutputAsync(
            "compiler", "{\"src\":\"x\"}", "exit code 1", "real output", egressDigest: null, CancellationToken.None);

        var tampered = attestation with { OutputHash = Sha256Hex("forged output") };

        var result = await service.VerifyAsync(tampered, CancellationToken.None);

        result.Should().BeFalse(
            "the output hash on a failure attestation must be covered by the HMAC signature, not a free-floating claim");
    }

    [Fact]
    public async Task SignFailureWithOutput_WithEgressDigest_CarriesDigestAndVerifies()
    {
        var service = CreateService();

        var attestation = await service.SignFailureWithOutputAsync(
            "fetcher", "{}", "exit code 7", "stdout before crash", egressDigest: "abc123", CancellationToken.None);

        attestation.EgressDigest.Should().Be("abc123");
        (await service.VerifyAsync(attestation, CancellationToken.None)).Should().BeTrue();

        var tampered = attestation with { EgressDigest = "def456" };
        (await service.VerifyAsync(tampered, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task LegacyFailureAttestation_WithoutOutputHash_StillVerifies()
    {
        var service = CreateService();
        var legacy = await service.SignFailureAsync("calculator", "{\"a\":1}", "timed out", CancellationToken.None);

        legacy.OutputHash.Should().BeNull();
        var result = await service.VerifyAsync(legacy, CancellationToken.None);

        result.Should().BeTrue("pre-existing failure attestations must remain verifiable after the payload extension");
    }

    private static string Sha256Hex(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
