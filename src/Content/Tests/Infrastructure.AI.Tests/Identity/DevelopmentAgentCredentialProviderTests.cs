using Domain.AI.Identity;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Identity;
using FluentAssertions;
using Infrastructure.AI.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Identity;

/// <summary>
/// Tests for <see cref="DevelopmentAgentCredentialProvider"/> — fixture identity
/// return, env-gating refusal, and missing-config refusal.
/// </summary>
public sealed class DevelopmentAgentCredentialProviderTests
{
    private static IOptionsMonitor<AppConfig> Config(DevelopmentProviderConfig? dev = null)
    {
        var cfg = new AppConfig
        {
            AI = new AIConfig
            {
                Identity = new AgentIdentityConfig
                {
                    Development = dev ?? new DevelopmentProviderConfig()
                }
            }
        };
        return Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == cfg);
    }

    private static IHostEnvironment EnvOf(string envName)
        => Mock.Of<IHostEnvironment>(e => e.EnvironmentName == envName);

    private static DevelopmentAgentCredentialProvider Build(
        DevelopmentProviderConfig? dev = null,
        string envName = "Development")
        => new(
            Config(dev),
            EnvOf(envName),
            NullLogger<DevelopmentAgentCredentialProvider>.Instance);

    [Fact]
    public void Kind_IsDevelopment()
    {
        Build().Kind.Should().Be(AgentIdentityKind.Development);
    }

    [Fact]
    public async Task ResolveAsync_NonDevelopmentEnvironment_ReturnsEnvFailureCode()
    {
        var provider = Build(envName: Environments.Production);

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Be(DevelopmentAgentCredentialProvider.EnvNotDevelopmentCode);
    }

    [Fact]
    public async Task ResolveAsync_StagingEnvironment_ReturnsEnvFailureCode()
    {
        var provider = Build(envName: Environments.Staging);

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(DevelopmentAgentCredentialProvider.EnvNotDevelopmentCode);
    }

    [Fact]
    public async Task ResolveAsync_MissingAgentId_ReturnsNotConfiguredCode()
    {
        var provider = Build(new DevelopmentProviderConfig { AgentId = "" });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(DevelopmentAgentCredentialProvider.NotConfiguredCode);
    }

    [Fact]
    public async Task ResolveAsync_WhitespaceAgentId_ReturnsNotConfiguredCode()
    {
        var provider = Build(new DevelopmentProviderConfig { AgentId = "   " });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(DevelopmentAgentCredentialProvider.NotConfiguredCode);
    }

    [Fact]
    public async Task ResolveAsync_DevelopmentEnvironmentWithConfig_ReturnsIdentity()
    {
        var provider = Build(new DevelopmentProviderConfig
        {
            AgentId = "dev-agent",
            TenantId = "dev-tenant",
            ObjectId = "00000000-dev-oid"
        });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Id.Should().Be("dev-agent");
        result.Value.Kind.Should().Be(AgentIdentityKind.Development);
        result.Value.TenantId.Should().Be("dev-tenant");
        result.Value.ObjectId.Should().Be("00000000-dev-oid");
        result.Value.Audience.Should().Be("api://x");
    }

    [Fact]
    public async Task ResolveAsync_EmptyAudienceContext_LeavesAudienceNull()
    {
        // Per design: empty string audience in CredentialContext maps to null on the
        // returned identity so consumers can distinguish "no audience" from "empty audience"
        // when introspecting AgentIdentity.Audience downstream.
        var provider = Build(new DevelopmentProviderConfig { AgentId = "dev-agent" });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = string.Empty },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Audience.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_NullContext_ThrowsArgumentNull()
    {
        var provider = Build();

        var act = () => provider.ResolveAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
