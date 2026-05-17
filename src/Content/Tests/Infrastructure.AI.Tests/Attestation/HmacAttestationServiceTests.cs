using System.Security.Cryptography;
using Domain.AI.Attestation;
using FluentAssertions;
using Infrastructure.AI.Attestation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Attestation;

public sealed class HmacAttestationServiceTests
{
    private static readonly string TestKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string TestKeyV2Base64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _timeProvider = new(FixedTime);

    private HmacAttestationService CreateService(AttestationKeyOptions? options = null)
    {
        options ??= new AttestationKeyOptions
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
    public async Task Sign_ProducesValidSignature()
    {
        var service = CreateService();

        var attestation = await service.SignAsync("calculator", "{\"a\":1}", "{\"result\":2}", CancellationToken.None);

        attestation.ToolName.Should().Be("calculator");
        attestation.InputHash.Should().NotBeNullOrEmpty().And.HaveLength(64);
        attestation.OutputHash.Should().NotBeNullOrEmpty().And.HaveLength(64);
        attestation.Signature.Should().NotBeNullOrEmpty();
        attestation.KeyVersion.Should().Be("v1");
        attestation.IsFailureAttestation.Should().BeFalse();
        attestation.FailureReason.Should().BeNull();
        attestation.Timestamp.Should().Be(FixedTime);
    }

    [Fact]
    public async Task Verify_AcceptsValidSignature()
    {
        var service = CreateService();
        var attestation = await service.SignAsync("calculator", "{\"a\":1}", "{\"result\":2}", CancellationToken.None);

        var result = await service.VerifyAsync(attestation, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Verify_RejectsTamperedOutput()
    {
        var service = CreateService();
        var attestation = await service.SignAsync("calculator", "{\"a\":1}", "{\"result\":2}", CancellationToken.None);

        var tampered = attestation with
        {
            OutputHash = attestation.OutputHash![..^1] + (attestation.OutputHash[^1] == 'a' ? 'b' : 'a')
        };

        var result = await service.VerifyAsync(tampered, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Verify_RejectsTamperedInput()
    {
        var service = CreateService();
        var attestation = await service.SignAsync("calculator", "{\"a\":1}", "{\"result\":2}", CancellationToken.None);

        var tampered = attestation with
        {
            InputHash = attestation.InputHash[..^1] + (attestation.InputHash[^1] == 'a' ? 'b' : 'a')
        };

        var result = await service.VerifyAsync(tampered, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Verify_RejectsTamperedTimestamp()
    {
        var service = CreateService();
        var attestation = await service.SignAsync("calculator", "{\"a\":1}", "{\"result\":2}", CancellationToken.None);

        var tampered = attestation with { Timestamp = attestation.Timestamp.AddSeconds(1) };

        var result = await service.VerifyAsync(tampered, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task SignFailure_SignsWithNullOutputHash()
    {
        var service = CreateService();

        var attestation = await service.SignFailureAsync("calculator", "{\"a\":1}", "OOM kill", CancellationToken.None);

        attestation.OutputHash.Should().BeNull();
        attestation.IsFailureAttestation.Should().BeTrue();
        attestation.FailureReason.Should().Be("OOM kill");
        attestation.InputHash.Should().NotBeNullOrEmpty().And.HaveLength(64);
        attestation.Signature.Should().NotBeNullOrEmpty();

        var verified = await service.VerifyAsync(attestation, CancellationToken.None);
        verified.Should().BeTrue();
    }

    [Fact]
    public async Task KeyRotation_OldKeyStillVerifies()
    {
        var optionsV1 = new AttestationKeyOptions
        {
            CurrentKeyVersion = "v1",
            HmacKeys =
            [
                new HmacKeyEntry { Version = "v1", Key = TestKeyBase64 },
                new HmacKeyEntry { Version = "v2", Key = TestKeyV2Base64 }
            ]
        };
        var serviceV1 = CreateService(optionsV1);
        var attestation = await serviceV1.SignAsync("calculator", "{\"a\":1}", "{\"result\":2}", CancellationToken.None);
        attestation.KeyVersion.Should().Be("v1");

        var optionsV2 = new AttestationKeyOptions
        {
            CurrentKeyVersion = "v2",
            HmacKeys =
            [
                new HmacKeyEntry { Version = "v1", Key = TestKeyBase64 },
                new HmacKeyEntry { Version = "v2", Key = TestKeyV2Base64 }
            ]
        };
        var serviceV2 = CreateService(optionsV2);

        var result = await serviceV2.VerifyAsync(attestation, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task KeyRotation_NewAttestationsUseCurrentKey()
    {
        var options = new AttestationKeyOptions
        {
            CurrentKeyVersion = "v2",
            HmacKeys =
            [
                new HmacKeyEntry { Version = "v1", Key = TestKeyBase64 },
                new HmacKeyEntry { Version = "v2", Key = TestKeyV2Base64 }
            ]
        };
        var service = CreateService(options);

        var attestation = await service.SignAsync("calculator", "{\"a\":1}", "{\"result\":2}", CancellationToken.None);

        attestation.KeyVersion.Should().Be("v2");
    }

    [Fact]
    public void Constructor_ThrowsWhenNoKeysConfigured()
    {
        var options = new AttestationKeyOptions
        {
            CurrentKeyVersion = "v1",
            HmacKeys = []
        };

        var act = () => CreateService(options);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*At least one HMAC key must be configured*");
    }

    [Fact]
    public void Constructor_ThrowsWhenCurrentVersionNotInKeychain()
    {
        var options = new AttestationKeyOptions
        {
            CurrentKeyVersion = "v99",
            HmacKeys = [new HmacKeyEntry { Version = "v1", Key = TestKeyBase64 }]
        };

        var act = () => CreateService(options);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*CurrentKeyVersion*");
    }

    [Fact]
    public async Task Verify_ReturnsFalse_WhenKeyVersionRetired()
    {
        var service = CreateService();
        var attestation = await service.SignAsync("calc", "{}", "{}", CancellationToken.None);

        var retiredOptions = new AttestationKeyOptions
        {
            CurrentKeyVersion = "v2",
            HmacKeys = [new HmacKeyEntry { Version = "v2", Key = TestKeyV2Base64 }]
        };
        var newService = CreateService(retiredOptions);

        var result = await newService.VerifyAsync(attestation, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Verify_RejectsTamperedFailureReason()
    {
        var service = CreateService();
        var attestation = await service.SignFailureAsync("calculator", "{\"a\":1}", "OOM kill", CancellationToken.None);

        var tampered = attestation with { FailureReason = "Permission denied" };

        var result = await service.VerifyAsync(tampered, CancellationToken.None);

        result.Should().BeFalse();
    }
}
