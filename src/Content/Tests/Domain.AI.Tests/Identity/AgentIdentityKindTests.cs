using Domain.AI.Identity;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Identity;

/// <summary>
/// Tests for <see cref="AgentIdentityKind"/> enum — confirms the credential-hierarchy
/// values exist in the expected order and that <c>Unspecified</c> is the default.
/// </summary>
public sealed class AgentIdentityKindTests
{
    [Fact]
    public void Default_IsUnspecified()
    {
        var defaultKind = default(AgentIdentityKind);

        defaultKind.Should().Be(AgentIdentityKind.Unspecified);
    }

    [Fact]
    public void Unspecified_HasValueZero()
    {
        ((int)AgentIdentityKind.Unspecified).Should().Be(0);
    }

    [Theory]
    [InlineData(AgentIdentityKind.Unspecified, 0)]
    [InlineData(AgentIdentityKind.FederatedCredential, 1)]
    [InlineData(AgentIdentityKind.ManagedIdentity, 2)]
    [InlineData(AgentIdentityKind.Certificate, 3)]
    [InlineData(AgentIdentityKind.ClientSecret, 4)]
    [InlineData(AgentIdentityKind.Development, 5)]
    public void Enum_HasExpectedNumericValues(AgentIdentityKind kind, int expectedValue)
    {
        ((int)kind).Should().Be(expectedValue);
    }

    [Fact]
    public void Enum_DefinesAllCredentialHierarchyKinds()
    {
        // The credential hierarchy per PR-1: federated -> managed identity -> certificate
        // -> client secret. Development is a test-only escape hatch. Unspecified is the
        // default sentinel.
        var values = Enum.GetValues<AgentIdentityKind>();

        values.Should().BeEquivalentTo(new[]
        {
            AgentIdentityKind.Unspecified,
            AgentIdentityKind.FederatedCredential,
            AgentIdentityKind.ManagedIdentity,
            AgentIdentityKind.Certificate,
            AgentIdentityKind.ClientSecret,
            AgentIdentityKind.Development
        });
    }
}
