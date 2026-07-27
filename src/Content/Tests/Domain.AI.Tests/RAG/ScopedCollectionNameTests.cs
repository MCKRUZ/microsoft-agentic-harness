using System.Text.RegularExpressions;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.RAG;

/// <summary>
/// Tests for <see cref="ScopedCollectionName"/> — the server-side derivation of per-tenant
/// RAG collection names. The derivation must be deterministic (same tenant → same name),
/// collision-safe (distinct tenants → distinct names even when their slugs sanitize
/// identically), and emit only store-safe characters.
/// </summary>
public sealed class ScopedCollectionNameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeriveForTenant_NullOrWhitespaceTenant_ReturnsNull(string? tenantId)
    {
        ScopedCollectionName.DeriveForTenant(tenantId).Should().BeNull(
            "a caller with no ambient tenant addresses the global/default collection");
    }

    [Fact]
    public void DeriveForTenant_SameTenant_IsDeterministic()
    {
        var first = ScopedCollectionName.DeriveForTenant("contoso");
        var second = ScopedCollectionName.DeriveForTenant("contoso");

        first.Should().Be(second);
    }

    [Fact]
    public void DeriveForTenant_CaseAndPaddingVariants_NormalizeToSameName()
    {
        var canonical = ScopedCollectionName.DeriveForTenant("contoso");

        ScopedCollectionName.DeriveForTenant("Contoso").Should().Be(canonical);
        ScopedCollectionName.DeriveForTenant("  contoso  ").Should().Be(canonical,
            "derivation mirrors the knowledge-memory scope key's trim + lowercase normalization");
    }

    [Fact]
    public void DeriveForTenant_DistinctTenantsWithIdenticalSlugs_ProduceDistinctNames()
    {
        // All three sanitize to the slug "a-b"; only the hash suffix can tell them apart.
        var names = new[] { "a b", "a-b", "a:b" }
            .Select(ScopedCollectionName.DeriveForTenant)
            .ToList();

        names.Should().OnlyHaveUniqueItems(
            "the SHA-256 suffix makes sanitization collisions impossible to exploit");
        names.Should().AllSatisfy(n => n.Should().StartWith("tenant-a-b-"));
    }

    [Theory]
    [InlineData("contoso")]
    [InlineData("Contoso Ltd: EU/West")]
    [InlineData("日本のテナント")]
    [InlineData("!!!")]
    public void DeriveForTenant_AnyTenant_EmitsStoreSafeName(string tenantId)
    {
        var name = ScopedCollectionName.DeriveForTenant(tenantId);

        name.Should().MatchRegex("^tenant-[a-z0-9-]+$",
            "the output alphabet must be valid for FAISS keys, SQLite values, and Azure index names");
        Regex.IsMatch(name!, "--").Should().BeFalse("dash runs are collapsed");
        name!.Length.Should().BeLessThanOrEqualTo(
            ScopedCollectionName.Prefix.Length + ScopedCollectionName.MaxSlugLength
            + 1 + ScopedCollectionName.HashLength);
    }

    [Fact]
    public void DeriveForTenant_HashSuffix_Is64Bits()
    {
        var name = ScopedCollectionName.DeriveForTenant("contoso")!;

        name[(name.LastIndexOf('-') + 1)..].Should().MatchRegex(
            $"^[0-9a-f]{{{ScopedCollectionName.HashLength}}}$",
            "8 hex chars (32 bits) would be offline-grindable for a crafted colliding tenant id; " +
            "the suffix must be 16 hex chars (64 bits)");
    }

    [Fact]
    public void DeriveForTenant_TenantWithNoUsableCharacters_UsesPlaceholderSlug()
    {
        var name = ScopedCollectionName.DeriveForTenant("!!!");

        name.Should().StartWith("tenant-t-",
            "an all-symbol tenant falls back to the 't' placeholder slug; the hash still disambiguates");
    }

    [Fact]
    public void DeriveForTenant_VeryLongTenant_CapsSlugLength()
    {
        var name = ScopedCollectionName.DeriveForTenant(new string('a', 500));

        name!.Length.Should().BeLessThanOrEqualTo(
            ScopedCollectionName.Prefix.Length + ScopedCollectionName.MaxSlugLength
            + 1 + ScopedCollectionName.HashLength);
    }

    [Fact]
    public void Resolve_AlreadyDerivedNameWithSameTenant_IsIdempotent()
    {
        // The MediatR handlers resolve before calling the orchestrator, which resolves
        // again at its choke point — double application must be a no-op.
        var derived = ScopedCollectionName.DeriveForTenant("contoso");

        ScopedCollectionName.Resolve(
                scopedCollectionsEnabled: true, requestedCollectionName: derived, tenantId: "contoso")
            .Should().Be(derived);
    }

    [Fact]
    public void Resolve_FeatureDisabled_ReturnsCallerSuppliedName()
    {
        ScopedCollectionName.Resolve(
                scopedCollectionsEnabled: false,
                requestedCollectionName: "corpus-a",
                tenantId: "contoso")
            .Should().Be("corpus-a", "when the feature is off, today's behavior is unchanged");
    }

    [Fact]
    public void Resolve_FeatureEnabled_IgnoresCallerSuppliedNameAndDerives()
    {
        var resolved = ScopedCollectionName.Resolve(
            scopedCollectionsEnabled: true,
            requestedCollectionName: "another-tenants-collection",
            tenantId: "contoso");

        resolved.Should().Be(ScopedCollectionName.DeriveForTenant("contoso"),
            "the effective collection is a pure function of the ambient tenant — never of request content");
    }

    [Fact]
    public void Resolve_FeatureEnabledWithoutTenant_ReturnsNullForGlobalCollection()
    {
        ScopedCollectionName.Resolve(
                scopedCollectionsEnabled: true,
                requestedCollectionName: null,
                tenantId: null)
            .Should().BeNull("no-identity callers go to the global/default collection, not an error");
    }
}
