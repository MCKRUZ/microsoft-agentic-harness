using Domain.AI.Agents;
using Domain.AI.Permissions;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Orchestration;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

public sealed class BuiltInSubagentProfilesTests
{
    private readonly BuiltInSubagentProfiles _registry;

    public BuiltInSubagentProfilesTests()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Orchestration = new OrchestrationConfig
                {
                    Subagent = new SubagentConfig
                    {
                        DefaultMaxTurnsPerSubagent = 25
                    }
                }
            }
        };

        var optionsMock = new Mock<IOptionsMonitor<AppConfig>>();
        optionsMock.Setup(o => o.CurrentValue).Returns(appConfig);

        _registry = new BuiltInSubagentProfiles(optionsMock.Object);
    }

    [Fact]
    public void GetProfile_Explore_HasReadOnlyTools()
    {
        var profile = _registry.GetProfile(SubagentType.Explore);

        profile.AgentType.Should().Be(SubagentType.Explore);
        profile.ToolAllowlist.Should().Contain("file_system");
        profile.MaxTurns.Should().Be(10);
        profile.PermissionMode.Should().Be(PermissionBehaviorType.Allow);
        profile.InheritParentTools.Should().BeTrue();
    }

    [Fact]
    public void GetProfile_Plan_HasNoTools()
    {
        var profile = _registry.GetProfile(SubagentType.Plan);

        profile.AgentType.Should().Be(SubagentType.Plan);
        profile.ToolAllowlist.Should().BeEmpty();
        profile.MaxTurns.Should().Be(3);
        profile.PermissionMode.Should().Be(PermissionBehaviorType.Allow);
        profile.InheritParentTools.Should().BeFalse();
    }

    [Fact]
    public void GetProfile_Verify_HasReadOnlyTools()
    {
        var profile = _registry.GetProfile(SubagentType.Verify);

        profile.AgentType.Should().Be(SubagentType.Verify);
        profile.ToolAllowlist.Should().Contain("file_system");
        profile.MaxTurns.Should().Be(5);
        profile.PermissionMode.Should().Be(PermissionBehaviorType.Allow);
    }

    [Fact]
    public void GetProfile_Execute_InheritsAllTools()
    {
        var profile = _registry.GetProfile(SubagentType.Execute);

        profile.AgentType.Should().Be(SubagentType.Execute);
        profile.ToolAllowlist.Should().BeNull();
        profile.MaxTurns.Should().Be(25);
        profile.PermissionMode.Should().Be(PermissionBehaviorType.Ask);
        profile.InheritParentTools.Should().BeTrue();
    }

    [Fact]
    public void GetProfile_General_HasModerateDefaults()
    {
        var profile = _registry.GetProfile(SubagentType.General);

        profile.AgentType.Should().Be(SubagentType.General);
        profile.ToolAllowlist.Should().BeNull();
        profile.MaxTurns.Should().Be(10);
        profile.PermissionMode.Should().Be(PermissionBehaviorType.Ask);
        profile.InheritParentTools.Should().BeTrue();
    }

    [Fact]
    public void GetAllProfiles_ReturnsAllTypes()
    {
        var profiles = _registry.GetAllProfiles();

        profiles.Should().HaveCount(5);
        profiles.Keys.Should().BeEquivalentTo(new[]
        {
            SubagentType.Explore,
            SubagentType.Plan,
            SubagentType.Verify,
            SubagentType.Execute,
            SubagentType.General
        });
    }

    [Fact]
    public void GetProfile_Execute_UsesConfiguredMaxTurns()
    {
        var profile = _registry.GetProfile(SubagentType.Execute);

        // The config was set to 25 in the constructor
        profile.MaxTurns.Should().Be(25);
    }
}
