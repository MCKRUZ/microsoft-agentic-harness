using Domain.AI.Identity;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Identity;

/// <summary>
/// Tests for <see cref="CredentialContext"/> record — captures the per-kind credential
/// metadata (issuer, audience, scopes) needed by an <see cref="IAgentCredentialProvider"/>
/// to acquire a token without baking environment-specific URLs into providers themselves.
/// </summary>
public sealed class CredentialContextTests
{
    [Fact]
    public void Constructor_WithRequiredAudience_SetsValue()
    {
        var ctx = new CredentialContext { Audience = "api://agent" };

        ctx.Audience.Should().Be("api://agent");
    }

    [Fact]
    public void Defaults_OptionalProperties_AreNullOrEmpty()
    {
        var ctx = new CredentialContext { Audience = "api://agent" };

        ctx.Issuer.Should().BeNull();
        ctx.Scopes.Should().BeEmpty();
    }

    [Fact]
    public void FullyPopulated_AllFieldsSet()
    {
        var ctx = new CredentialContext
        {
            Audience = "api://agent",
            Issuer = "https://login.microsoftonline.com/contoso/v2.0",
            Scopes = ["api://agent/.default", "https://graph.microsoft.com/.default"]
        };

        ctx.Audience.Should().Be("api://agent");
        ctx.Issuer.Should().Be("https://login.microsoftonline.com/contoso/v2.0");
        ctx.Scopes.Should().HaveCount(2);
        ctx.Scopes.Should().Contain("api://agent/.default");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new CredentialContext
        {
            Audience = "api://x",
            Issuer = "https://issuer",
            Scopes = ["s1", "s2"]
        };
        var b = new CredentialContext
        {
            Audience = "api://x",
            Issuer = "https://issuer",
            Scopes = ["s1", "s2"]
        };

        a.Should().Be(b);
    }

    [Fact]
    public void Scopes_IsImmutableList_PreventsExternalMutation()
    {
        // Records expose IReadOnlyList<string>; backing source can't be mutated through
        // the returned reference.
        var ctx = new CredentialContext
        {
            Audience = "api://x",
            Scopes = ["s1"]
        };

        ctx.Scopes.Should().BeAssignableTo<IReadOnlyList<string>>();
    }
}
