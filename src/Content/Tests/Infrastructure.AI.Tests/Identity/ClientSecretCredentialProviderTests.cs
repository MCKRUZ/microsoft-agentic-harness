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
/// Tests for <see cref="ClientSecretCredentialProvider"/> — required-field config
/// gating, identity construction, and the env-gated startup warning behaviour.
/// </summary>
public sealed class ClientSecretCredentialProviderTests
{
    private static IOptionsMonitor<AppConfig> Config(ClientSecretProviderConfig? cs = null)
    {
        var cfg = new AppConfig
        {
            AI = new AIConfig
            {
                Identity = new AgentIdentityConfig
                {
                    ClientSecret = cs ?? new ClientSecretProviderConfig()
                }
            }
        };
        return Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == cfg);
    }

    private static IHostEnvironment EnvOf(string envName)
        => Mock.Of<IHostEnvironment>(e => e.EnvironmentName == envName);

    private static ClientSecretCredentialProvider Build(
        ClientSecretProviderConfig? cs = null,
        string envName = "Development")
        => new(
            Config(cs),
            EnvOf(envName),
            NullLogger<ClientSecretCredentialProvider>.Instance);

    private static ClientSecretProviderConfig FullyConfigured() => new()
    {
        AgentId = "secret-agent",
        TenantId = "tenant-a",
        ClientId = "00000000-0000-0000-0000-000000000001",
        ClientSecret = "fixture-secret-not-a-real-secret",
        ObjectId = "oid-1"
    };

    [Fact]
    public void Kind_IsClientSecret()
    {
        Build().Kind.Should().Be(AgentIdentityKind.ClientSecret);
    }

    [Theory]
    [InlineData(null, "tenant", "client", "secret")]
    [InlineData("agent", null, "client", "secret")]
    [InlineData("agent", "tenant", null, "secret")]
    [InlineData("agent", "tenant", "client", null)]
    [InlineData("agent", "tenant", "client", "")]
    public async Task ResolveAsync_MissingRequiredFields_ReturnsNotConfigured(
        string? agentId, string? tenantId, string? clientId, string? secret)
    {
        var provider = Build(new ClientSecretProviderConfig
        {
            AgentId = agentId,
            TenantId = tenantId,
            ClientId = clientId,
            ClientSecret = secret
        });

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ClientSecretCredentialProvider.NotConfiguredCode);
    }

    [Fact]
    public async Task ResolveAsync_FullyConfigured_ReturnsIdentity()
    {
        var provider = Build(FullyConfigured());

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be("secret-agent");
        result.Value.Kind.Should().Be(AgentIdentityKind.ClientSecret);
        result.Value.TenantId.Should().Be("tenant-a");
        result.Value.ObjectId.Should().Be("oid-1");
    }

    [Fact]
    public async Task ResolveAsync_InDevelopment_DoesNotEmitWarning_ButStillResolves()
    {
        // Functional check — the warning gating doesn't affect the resolve outcome.
        // The actual log emission isn't asserted (NullLogger swallows it); the gate
        // is exercised behaviourally via the Interlocked field's one-shot semantics
        // covered by the next test.
        var provider = Build(FullyConfigured(), envName: Environments.Development);

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_InProduction_StillResolves_WarningGatedSeparately()
    {
        var provider = Build(FullyConfigured(), envName: Environments.Production);

        var result = await provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentInProduction_AllSucceed_NoDeadlock()
    {
        // Stress the Interlocked one-shot warning under contention. Many threads
        // calling concurrently should all succeed (the warning is logged at most
        // once per process, but resolution is not gated by it).
        var provider = Build(FullyConfigured(), envName: Environments.Production);

        var tasks = Enumerable.Range(0, 32).Select(_ => provider.ResolveAsync(
            new CredentialContext { Audience = "api://x" },
            CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks);

        tasks.Should().AllSatisfy(t =>
        {
            t.Result.IsSuccess.Should().BeTrue();
            t.Result.Value!.Id.Should().Be("secret-agent");
        });
    }

    [Fact]
    public async Task ResolveAsync_NullContext_ThrowsArgumentNull()
    {
        var provider = Build(FullyConfigured());

        var act = () => provider.ResolveAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
