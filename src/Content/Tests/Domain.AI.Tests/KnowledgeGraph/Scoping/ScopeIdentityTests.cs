using Domain.AI.KnowledgeGraph.Scoping;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.KnowledgeGraph.Scoping;

/// <summary>
/// Verifies that <see cref="ScopeIdentity"/> produces a single canonical form for owner
/// and tenant identifiers so authorization gates and storage filters agree.
/// </summary>
public class ScopeIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Canonicalize_AbsentIdentity_ReturnsNull(string? input)
    {
        ScopeIdentity.Canonicalize(input).Should().BeNull();
    }

    [Theory]
    [InlineData("Alice", "alice")]
    [InlineData("ALICE", "alice")]
    [InlineData("  Alice  ", "alice")]
    [InlineData("ABC-123-DEF", "abc-123-def")]
    [InlineData("User@Example.COM", "user@example.com")]
    public void Canonicalize_MixedCaseOrPadded_ReturnsTrimmedLowercase(string input, string expected)
    {
        ScopeIdentity.Canonicalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Alice", "alice")]
    [InlineData("ALICE", "  alice  ")]
    [InlineData(null, "")]
    [InlineData("", "   ")]
    public void AreSame_EquivalentAfterCanonicalization_ReturnsTrue(string? left, string? right)
    {
        ScopeIdentity.AreSame(left, right).Should().BeTrue();
    }

    [Theory]
    [InlineData("alice", "bob")]
    [InlineData("alice", null)]
    [InlineData("owner-1", "owner-2")]
    public void AreSame_DistinctPrincipals_ReturnsFalse(string? left, string? right)
    {
        ScopeIdentity.AreSame(left, right).Should().BeFalse();
    }
}
