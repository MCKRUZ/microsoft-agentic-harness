using Application.AI.Common.Interfaces.Identity;
using Domain.AI.Identity;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Identity;
using FluentAssertions;
using Infrastructure.AI.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Identity;

/// <summary>
/// Tests for <see cref="ToolAuthorizationConfigValidator"/> — the startup check that turns a
/// tool-authorization configuration which could only ever deny into a boot failure.
/// </summary>
/// <remarks>
/// The gate this guards is fail-closed by design, so every misconfiguration below produces the same
/// runtime symptom: an agent that starts cleanly and then refuses to do anything, with the reason
/// visible only in structured logs. Each of these cases is a mistake rather than a policy, and boot
/// is the honest place to say so.
/// </remarks>
public sealed class ToolAuthorizationConfigValidatorTests
{
    [Fact]
    public async Task StartAsync_FeatureOff_NoOpsEvenWhenNothingElseIsConfigured()
    {
        // A consumer exploring the template must not be blocked from running the host.
        var validator = Build(new ToolAuthorizationConfig { Enabled = false }, identityEnabled: false);

        var act = async () => await validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_EnabledButIdentitySubsystemOff_Throws()
    {
        var validator = Build(Allowlist(enabled: true), identityEnabled: false);

        var act = async () => await validator.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*AI.Identity.Enabled is false*",
                "with no identity subsystem there is nothing to authorize and every call would be "
                + "refused, which is a configuration mistake rather than a policy");
    }

    [Fact]
    public async Task StartAsync_EnabledWithEmptyAllowlist_Throws()
    {
        var validator = Build(new ToolAuthorizationConfig { Enabled = true }, identityEnabled: true);

        var act = async () => await validator.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*AllowedToolsByAgentId is empty*",
                "an empty allowlist denies every agent every tool, which is the correct fail-closed "
                + "reading and almost never what switching the feature on was meant to do");
    }

    [Fact]
    public async Task StartAsync_EnabledWithBlankToolKey_Throws()
    {
        var config = new ToolAuthorizationConfig { Enabled = true };
        config.AllowedToolsByAgentId["agent-1"] = ["file_system", "  "];
        var validator = Build(config, identityEnabled: true);

        var act = async () => await validator.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*blank tool key*");
    }

    [Fact]
    public async Task StartAsync_EnabledButValidatorNotRegistered_Throws()
    {
        var validator = Build(Allowlist(enabled: true), identityEnabled: true, registerServices: false);

        var act = async () => await validator.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*IAgentIdentityValidator*",
                "the feature is on but nothing can answer the policy question, so every call would "
                + "be refused for want of an oracle");
    }

    [Fact]
    public async Task StartAsync_FullyConfigured_DoesNotThrow()
    {
        // The mutation control for every case above: if this threw too, the assertions would be
        // passing for the wrong reason.
        var validator = Build(Allowlist(enabled: true), identityEnabled: true);

        var act = async () => await validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_AgentMappedToAnEmptyList_IsAllowedThrough()
    {
        // Valid policy — the documented way to keep an agent in the allowlist while granting it
        // nothing. It is logged rather than rejected, because refusing it would make a legitimate
        // configuration unbootable.
        var config = new ToolAuthorizationConfig { Enabled = true };
        config.AllowedToolsByAgentId["agent-1"] = ["file_system"];
        config.AllowedToolsByAgentId["agent-revoked"] = [];
        var validator = Build(config, identityEnabled: true);

        var act = async () => await validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static ToolAuthorizationConfig Allowlist(bool enabled)
    {
        var config = new ToolAuthorizationConfig { Enabled = enabled };
        config.AllowedToolsByAgentId["agent-1"] = ["file_system"];
        return config;
    }

    private static ToolAuthorizationConfigValidator Build(
        ToolAuthorizationConfig toolAuthorization,
        bool identityEnabled,
        bool registerServices = true)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Identity = new AgentIdentityConfig
                {
                    Enabled = identityEnabled,
                    ToolAuthorization = toolAuthorization
                }
            }
        };

        var services = new ServiceCollection();
        if (registerServices)
        {
            services.AddSingleton<IAgentIdentityValidator>(new StubValidator());
            services.AddSingleton<IAgentIdentityResolver>(new StubResolver());
        }

        return new ToolAuthorizationConfigValidator(
            services.BuildServiceProvider(),
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig),
            NullLogger<ToolAuthorizationConfigValidator>.Instance);
    }

    private sealed class StubValidator : IAgentIdentityValidator
    {
        public bool CanInvoke(AgentIdentity identity, string toolKey) => true;
    }

    private sealed class StubResolver : IAgentIdentityResolver
    {
        public Task<Result<AgentIdentity>> ResolveAsync(
            CredentialContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Result<AgentIdentity>.Success(
                new AgentIdentity { Id = "agent-1", Kind = AgentIdentityKind.ManagedIdentity }));
    }
}
