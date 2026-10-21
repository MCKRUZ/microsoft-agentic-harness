using Domain.AI.KnowledgeGraph.Scoping;
using Domain.Common.Compliance;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Compliance;

/// <summary>
/// <see cref="ErasureSubject.CanonicalOwnerId"/> must be exactly what <see cref="ScopeIdentity.Canonicalize"/> produces
/// (#900). Plans, schedules and the knowledge graph stamp owners through <c>ScopeIdentity</c>; <c>Domain.Common</c> cannot
/// reference it, so the subject computes the same form itself. If the two ever differ, erasure of those stores matches
/// nothing and still reports success.
/// </summary>
public sealed class ErasureSubjectCanonicalFormTests
{
    [Theory]
    [InlineData("user-1")]
    [InlineData("User-1")]
    [InlineData("  MIXED.Case@Example.COM  ")]
    [InlineData("İstanbul-user")]
    [InlineData("\tTab\t")]
    public void CanonicalOwnerId_IsWhatScopeIdentityProduces(string ownerId)
    {
        new ErasureSubject(ownerId, []).CanonicalOwnerId.Should().Be(ScopeIdentity.Canonicalize(ownerId));
    }

    [Fact]
    public void OwnerId_IsKeptExactlyAsIssued()
    {
        // Conversations, schedules' API callers and ingested chunks store the id as issued; trimming or folding it here
        // would make those erasers miss.
        new ErasureSubject("  User-1 ", []).OwnerId.Should().Be("  User-1 ");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankOwner_IsRefused(string ownerId)
    {
        var act = () => new ErasureSubject(ownerId, []);

        act.Should().Throw<ArgumentException>("a blank subject is never a wildcard");
    }
}
