using Domain.Common.Config.AI.MCP;
using FluentAssertions;
using Infrastructure.AI.MCPServer.Authentication;
using Xunit;

namespace Infrastructure.AI.MCPServer.Tests.Authentication;

/// <summary>
/// Unit tests for the shared-key authentication building blocks: constant-time
/// credential comparison, options validation, and the server-side config validity
/// rules that back the fail-closed startup check.
/// </summary>
public sealed class McpSharedKeyAuthenticationTests
{
    // -- CredentialsMatch --

    [Fact]
    public void CredentialsMatch_SameValue_ReturnsTrue() =>
        McpSharedKeyAuthenticationHandler.CredentialsMatch("key-123", "key-123")
            .Should().BeTrue();

    [Theory]
    [InlineData("key-123", "key-124")]
    [InlineData("key-123", "key-1234")]
    [InlineData("key-123", "")]
    [InlineData("", "key-123")]
    [InlineData("KEY-123", "key-123")]
    public void CredentialsMatch_DifferentValues_ReturnsFalse(string presented, string expected) =>
        McpSharedKeyAuthenticationHandler.CredentialsMatch(presented, expected)
            .Should().BeFalse();

    // -- Options validation --

    [Fact]
    public void OptionsValidate_MissingExpectedCredential_Throws()
    {
        var options = new McpSharedKeyAuthenticationOptions { HeaderName = "X-API-Key" };

        Action act = options.Validate;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ExpectedCredential*");
    }

    [Fact]
    public void OptionsValidate_MissingHeaderName_Throws()
    {
        var options = new McpSharedKeyAuthenticationOptions
        {
            HeaderName = " ",
            ExpectedCredential = "test-key-not-a-real-secret"
        };

        Action act = options.Validate;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*HeaderName*");
    }

    [Fact]
    public void OptionsValidate_FullyConfigured_DoesNotThrow()
    {
        var options = new McpSharedKeyAuthenticationOptions
        {
            HeaderName = "X-API-Key",
            ExpectedCredential = "test-key-not-a-real-secret"
        };

        Action act = options.Validate;

        act.Should().NotThrow();
    }

    // -- Server-side config validity (backs the fail-closed startup check) --

    [Fact]
    public void IsValidForServer_None_IsTrue() =>
        new McpServerAuthConfig().IsValidForServer.Should().BeTrue();

    [Fact]
    public void IsValidForServer_ApiKeyWithoutKey_IsFalse() =>
        new McpServerAuthConfig { Type = McpServerAuthType.ApiKey }
            .IsValidForServer.Should().BeFalse();

    [Fact]
    public void IsValidForServer_ApiKeyWithKey_IsTrue() =>
        new McpServerAuthConfig
        {
            Type = McpServerAuthType.ApiKey,
            ApiKey = "test-key-not-a-real-secret"
        }.IsValidForServer.Should().BeTrue();

    [Fact]
    public void IsValidForServer_BearerWithoutToken_IsFalse() =>
        new McpServerAuthConfig { Type = McpServerAuthType.Bearer }
            .IsValidForServer.Should().BeFalse();

    [Fact]
    public void IsValidForServer_BearerWithToken_IsTrue() =>
        new McpServerAuthConfig
        {
            Type = McpServerAuthType.Bearer,
            BearerToken = "test-token-not-a-real-secret"
        }.IsValidForServer.Should().BeTrue();

    [Theory]
    [InlineData(null, "client-id")]
    [InlineData("tenant-id", null)]
    [InlineData(null, null)]
    public void IsValidForServer_EntraMissingTenantOrClient_IsFalse(string? tenantId, string? clientId) =>
        new McpServerAuthConfig
        {
            Type = McpServerAuthType.Entra,
            TenantId = tenantId,
            ClientId = clientId
        }.IsValidForServer.Should().BeFalse();

    [Fact]
    public void IsValidForServer_EntraWithTenantAndClient_IsTrue_WithoutClientSecretOrScopes() =>
        new McpServerAuthConfig
        {
            Type = McpServerAuthType.Entra,
            TenantId = "tenant-id",
            ClientId = "client-id"
        }.IsValidForServer.Should().BeTrue(
            "server-side validation only checks issuer/audience material — it never mints tokens");

    [Fact]
    public void AllowAnonymous_DefaultsToFalse() =>
        new McpServerAuthConfig().AllowAnonymous.Should().BeFalse(
            "the MCP server must be fail-closed by default");
}
