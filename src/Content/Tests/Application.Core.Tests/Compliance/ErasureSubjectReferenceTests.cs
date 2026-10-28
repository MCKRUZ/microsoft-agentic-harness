using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Services.Compliance;
using Application.Core.Validation;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.Compliance;

/// <summary>
/// The pseudonymous subject reference (#900): recomputable by whoever holds the key, unlinkable without it, absent when
/// no key is set, and an unusable key is refused at startup and at use rather than producing a weak reference.
/// </summary>
public sealed class ErasureSubjectReferenceTests
{
    private static readonly byte[] KeyBytes = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static HmacErasureSubjectReference With(string key) => new(
        Mock.Of<IOptionsMonitor<ErasureConfig>>(m => m.CurrentValue == new ErasureConfig { SubjectReferenceKey = key }));

    [Fact]
    public void For_KeySet_IsTheHmacOfTheIdAsIssued()
    {
        var reference = With(Convert.ToBase64String(KeyBytes)).For("Subject-1");

        reference.Should().Be(Convert.ToHexStringLower(HMACSHA256.HashData(KeyBytes, Encoding.UTF8.GetBytes("Subject-1"))));
        reference.Should().NotContain("Subject-1");
    }

    [Fact]
    public void For_DifferentKeys_GiveUnrelatedReferences()
    {
        var other = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

        With(Convert.ToBase64String(KeyBytes)).For("Subject-1")
            .Should().NotBe(With(Convert.ToBase64String(other)).For("Subject-1"),
                "without the key, a reference cannot be recomputed from a guessed id");
    }

    [Fact]
    public void For_NoKey_RecordsNoReference()
    {
        With(string.Empty).For("Subject-1").Should().BeNull();
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("AAEC")]
    [InlineData("not base64!")]
    public void For_UnusableKey_Throws(string key)
    {
        // A whitespace key decodes to nothing, which would make the reference a plain hash a list of ids can reverse.
        var act = () => With(key).For("Subject-1");

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAEC")]
    [InlineData("   ")]
    public void Validator_MalformedShortOrBlankKey_IsRefused(string key)
    {
        new ErasureConfigValidator().Validate(new ErasureConfig { SubjectReferenceKey = key }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_EmptyOr32ByteKey_IsAccepted()
    {
        var validator = new ErasureConfigValidator();

        validator.Validate(new ErasureConfig()).IsValid.Should().BeTrue();
        validator.Validate(new ErasureConfig { SubjectReferenceKey = Convert.ToBase64String(KeyBytes) })
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" tenant-a")]
    public void Validator_BlankOrPaddedOperatorTenant_IsRefused(string tenantId)
    {
        new ErasureConfigValidator().Validate(new ErasureConfig { OperatorTenantIds = [tenantId] })
            .IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validator_BlankComplianceTrailFolder_IsRefused(string path)
    {
        // #969: without a folder, erasures, exports and removals would leave no durable proof at all.
        new ErasureConfigValidator().Validate(new ErasureConfig { AuditStoragePath = path })
            .Errors.Should().ContainSingle(e => e.PropertyName == nameof(ErasureConfig.AuditStoragePath));
    }

    [Fact]
    public void ComplianceTrailFolder_DefaultsBesideTheOtherAuditTrails()
    {
        new ErasureConfig().AuditStoragePath.Should().Be(".agent-sessions/compliance",
            "durable by default: a host that sets nothing still keeps the proof");
    }
}
