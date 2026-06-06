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
/// Tests for <see cref="EntraAgentIdentityValidator"/> — fail-closed default,
/// per-agent allowlist matching, wildcard semantics, and the deny-event surface.
/// </summary>
public sealed class EntraAgentIdentityValidatorTests
{
    private static IOptionsMonitor<AppConfig> Config(ToolAuthorizationConfig? auth = null)
    {
        var cfg = new AppConfig
        {
            AI = new AIConfig
            {
                Identity = new AgentIdentityConfig
                {
                    ToolAuthorization = auth ?? new ToolAuthorizationConfig()
                }
            }
        };
        return Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == cfg);
    }

    private static EntraAgentIdentityValidator Build(ToolAuthorizationConfig? auth = null)
        => new(Config(auth), NullLogger<EntraAgentIdentityValidator>.Instance);

    private static AgentIdentity Identity(string id, AgentIdentityKind kind = AgentIdentityKind.ManagedIdentity)
        => new() { Id = id, Kind = kind };

    private static ToolAuthorizationConfig AllowlistOf(params (string AgentId, string[] Tools)[] entries)
    {
        var auth = new ToolAuthorizationConfig();
        foreach (var (agentId, tools) in entries)
            auth.AllowedToolsByAgentId[agentId] = tools;
        return auth;
    }

    [Fact]
    public void CanInvoke_NullIdentity_ThrowsArgumentNull()
    {
        var validator = Build();

        var act = () => validator.CanInvoke(null!, "file_system");

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanInvoke_NullOrWhitespaceToolKey_Denies(string? toolKey)
    {
        var validator = Build(AllowlistOf(("agent-1", ["file_system"])));

        validator.CanInvoke(Identity("agent-1"), toolKey!).Should().BeFalse();
    }

    [Fact]
    public void CanInvoke_UnspecifiedKind_Denies()
    {
        var validator = Build(AllowlistOf(("agent-1", ["file_system"])));

        validator.CanInvoke(Identity("agent-1", AgentIdentityKind.Unspecified), "file_system")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanInvoke_IdentityIdMissing_Denies(string? id)
    {
        var validator = Build(AllowlistOf(("agent-1", ["file_system"])));

        validator.CanInvoke(
            new AgentIdentity { Id = id!, Kind = AgentIdentityKind.ManagedIdentity },
            "file_system").Should().BeFalse();
    }

    [Fact]
    public void CanInvoke_NoAllowlistConfig_Denies()
    {
        var validator = Build(); // empty ToolAuthorizationConfig

        validator.CanInvoke(Identity("agent-1"), "file_system").Should().BeFalse();
    }

    [Fact]
    public void CanInvoke_AgentNotInAllowlist_Denies()
    {
        var validator = Build(AllowlistOf(("other-agent", ["file_system"])));

        validator.CanInvoke(Identity("agent-1"), "file_system").Should().BeFalse();
    }

    [Fact]
    public void CanInvoke_AgentMappedToEmptyList_Denies()
    {
        var validator = Build(AllowlistOf(("agent-1", [])));

        validator.CanInvoke(Identity("agent-1"), "file_system").Should().BeFalse();
    }

    [Fact]
    public void CanInvoke_ToolInAllowlist_Allows()
    {
        var validator = Build(AllowlistOf(("agent-1", ["file_system", "calculation_engine"])));

        validator.CanInvoke(Identity("agent-1"), "file_system").Should().BeTrue();
        validator.CanInvoke(Identity("agent-1"), "calculation_engine").Should().BeTrue();
    }

    [Fact]
    public void CanInvoke_ToolNotInAllowlist_Denies()
    {
        var validator = Build(AllowlistOf(("agent-1", ["file_system"])));

        validator.CanInvoke(Identity("agent-1"), "calculation_engine").Should().BeFalse();
    }

    [Fact]
    public void CanInvoke_Wildcard_AllowsAnyTool()
    {
        var validator = Build(AllowlistOf(("agent-1", [ToolAuthorizationConfig.WildcardToken])));

        validator.CanInvoke(Identity("agent-1"), "file_system").Should().BeTrue();
        validator.CanInvoke(Identity("agent-1"), "calculation_engine").Should().BeTrue();
        validator.CanInvoke(Identity("agent-1"), "newly_added_tool").Should().BeTrue();
    }

    [Fact]
    public void CanInvoke_WildcardAlongsideSpecific_StillAllowsAll()
    {
        // A config like ["file_system", "*"] is unusual but legal — wildcard wins.
        var validator = Build(AllowlistOf(("agent-1", ["file_system", ToolAuthorizationConfig.WildcardToken])));

        validator.CanInvoke(Identity("agent-1"), "anything_at_all").Should().BeTrue();
    }

    [Fact]
    public void CanInvoke_AgentIdMatchingIsCaseInsensitive()
    {
        // Entra app names and config-file casing drift in practice.
        var validator = Build(AllowlistOf(("Agent-One", ["file_system"])));

        validator.CanInvoke(Identity("AGENT-ONE"), "file_system").Should().BeTrue();
        validator.CanInvoke(Identity("agent-one"), "file_system").Should().BeTrue();
    }

    [Fact]
    public void CanInvoke_ToolKeyMatchingIsCaseSensitive()
    {
        // Tool keys are the keyed-DI registration strings — exact match.
        var validator = Build(AllowlistOf(("agent-1", ["file_system"])));

        validator.CanInvoke(Identity("agent-1"), "File_System").Should().BeFalse();
        validator.CanInvoke(Identity("agent-1"), "FILE_SYSTEM").Should().BeFalse();
    }

    [Fact]
    public void CanInvoke_MultipleAgentsInConfig_OnlyRequestedAgentsPolicyConsulted()
    {
        var validator = Build(AllowlistOf(
            ("agent-1", ["file_system"]),
            ("agent-2", ["calculation_engine"])));

        validator.CanInvoke(Identity("agent-1"), "file_system").Should().BeTrue();
        validator.CanInvoke(Identity("agent-1"), "calculation_engine").Should().BeFalse();
        validator.CanInvoke(Identity("agent-2"), "calculation_engine").Should().BeTrue();
        validator.CanInvoke(Identity("agent-2"), "file_system").Should().BeFalse();
    }
}
