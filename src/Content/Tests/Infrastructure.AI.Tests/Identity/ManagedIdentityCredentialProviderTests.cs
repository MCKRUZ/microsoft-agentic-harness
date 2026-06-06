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
/// Tests for <see cref="ManagedIdentityCredentialProvider"/> — config gating and
/// identity construction. The underlying <c>ManagedIdentityCredential</c> is not
/// exercised at resolve time (its <c>GetToken</c> is deferred).
/// </summary>
public sealed class ManagedIdentityCredentialProviderTests
{
    private static IOptionsMonitor<AppConfig> Config(ManagedIdentityProviderConfig? mi = null)
    {
        var cfg = new AppConfig
        {
            AI = new AIConfig
            {
                Identity = new AgentIdentityConfig
                {
                    ManagedIdentity = mi ?? new ManagedIdentityProviderConfig()
                }
            }
        };
        return Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == cfg);
    }

    private static ManagedIdentityCredentialProvider Build(ManagedIdentityProviderConfig? mi = null)
        => new(Config(mi), NullLogger<ManagedIdentityCredentialProvider>.Instance);

    [Fact]
    public void Kind_IsManagedIdentity()
    {
        Build().Kind.Should().Be(AgentIdentityKind.ManagedIdentity);
    }

    [Fact]
    public async Task ResolveAsync_NoConfig_ReturnsNotConfigured()
    {
        var provider = Build();

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Be(ManagedIdentityCredentialProvider.NotConfiguredCode);
    }

    [Fact]
    public async Task ResolveAsync_WhitespaceAgentId_ReturnsNotConfigured()
    {
        var provider = Build(new ManagedIdentityProviderConfig { AgentId = "  " });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ManagedIdentityCredentialProvider.NotConfiguredCode);
    }

    [Fact]
    public async Task ResolveAsync_SystemAssigned_ReturnsIdentity()
    {
        // ClientId null → system-assigned MI
        var provider = Build(new ManagedIdentityProviderConfig
        {
            AgentId = "system-mi-agent",
            TenantId = "tenant-a",
            ObjectId = "oid-1"
        });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("system-mi-agent");
        result.Value.Kind.Should().Be(AgentIdentityKind.ManagedIdentity);
        result.Value.TenantId.Should().Be("tenant-a");
        result.Value.ObjectId.Should().Be("oid-1");
        result.Value.Audience.Should().Be("api://x");
    }

    [Fact]
    public async Task ResolveAsync_UserAssigned_ReturnsIdentity()
    {
        var provider = Build(new ManagedIdentityProviderConfig
        {
            AgentId = "ua-mi-agent",
            ClientId = "00000000-0000-0000-0000-000000000001",
            TenantId = "tenant-b"
        });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("ua-mi-agent");
        result.Value.TenantId.Should().Be("tenant-b");
    }

    [Fact]
    public async Task ResolveAsync_NullContext_ThrowsArgumentNull()
    {
        var provider = Build(new ManagedIdentityProviderConfig { AgentId = "x" });

        var act = () => provider.ResolveAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
