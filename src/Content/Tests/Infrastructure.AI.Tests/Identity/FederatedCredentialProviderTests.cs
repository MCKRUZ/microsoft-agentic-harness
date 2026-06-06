using Domain.AI.Identity;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Identity;
using FluentAssertions;
using Infrastructure.AI.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Identity;

/// <summary>
/// Tests for <see cref="FederatedCredentialProvider"/> — required-field config
/// gating and identity construction.
/// </summary>
public sealed class FederatedCredentialProviderTests
{
    private static IOptionsMonitor<AppConfig> Config(FederatedProviderConfig? fed = null)
    {
        var cfg = new AppConfig
        {
            AI = new AIConfig
            {
                Identity = new AgentIdentityConfig
                {
                    FederatedCredential = fed ?? new FederatedProviderConfig()
                }
            }
        };
        return Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == cfg);
    }

    private static FederatedCredentialProvider Build(FederatedProviderConfig? fed = null)
        => new(Config(fed), NullLogger<FederatedCredentialProvider>.Instance);

    private static FederatedProviderConfig FullyConfigured() => new()
    {
        AgentId = "fed-agent",
        TenantId = "tenant-a",
        ClientId = "00000000-0000-0000-0000-000000000001",
        TokenFilePath = "/var/run/secrets/azure/tokens/azure-identity-token",
        ObjectId = "oid-1"
    };

    [Fact]
    public void Kind_IsFederatedCredential()
    {
        Build().Kind.Should().Be(AgentIdentityKind.FederatedCredential);
    }

    [Theory]
    [InlineData(null, "tenant", "client")]
    [InlineData("agent", null, "client")]
    [InlineData("agent", "tenant", null)]
    [InlineData("", "tenant", "client")]
    [InlineData("agent", "", "client")]
    [InlineData("agent", "tenant", "")]
    public async Task ResolveAsync_MissingRequiredFields_ReturnsNotConfigured(
        string? agentId, string? tenantId, string? clientId)
    {
        var provider = Build(new FederatedProviderConfig
        {
            AgentId = agentId,
            TenantId = tenantId,
            ClientId = clientId
        });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(FederatedCredentialProvider.NotConfiguredCode);
    }

    [Fact]
    public async Task ResolveAsync_AllRequiredPresent_ReturnsIdentity()
    {
        var provider = Build(FullyConfigured());

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("fed-agent");
        result.Value.Kind.Should().Be(AgentIdentityKind.FederatedCredential);
        result.Value.TenantId.Should().Be("tenant-a");
        result.Value.ObjectId.Should().Be("oid-1");
        result.Value.Audience.Should().Be("api://x");
    }

    [Fact]
    public async Task ResolveAsync_NullTokenFilePath_StillConstructs()
    {
        // Null TokenFilePath means the SDK reads AZURE_FEDERATED_TOKEN_FILE env var.
        // Provider should not reject — that's the AKS default and a normal config.
        var config = FullyConfigured();
        config.TokenFilePath = null;
        var provider = Build(config);

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_NullContext_ThrowsArgumentNull()
    {
        var provider = Build(FullyConfigured());

        var act = () => provider.ResolveAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
