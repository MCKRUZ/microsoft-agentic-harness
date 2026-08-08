using Application.AI.Common.Interfaces.Agents;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Permissions;
using FluentAssertions;
using Infrastructure.AI.Governance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Governance;

public sealed class DefaultAutonomyTierResolverTests
{
    private readonly Mock<ISubagentProfileRegistry> _registryMock = new();
    private readonly Mock<ILogger<DefaultAutonomyTierResolver>> _loggerMock = new();

    private DefaultAutonomyTierResolver CreateResolver(string defaultLevel = "Supervised")
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Permissions = new PermissionsConfig
                {
                    DefaultAutonomyLevel = defaultLevel
                }
            }
        };

        var optionsMonitor = Mock.Of<IOptionsMonitor<AppConfig>>(
            o => o.CurrentValue == appConfig);

        return new DefaultAutonomyTierResolver(
            _registryMock.Object,
            optionsMonitor,
            _loggerMock.Object);
    }

    [Fact]
    public void Resolve_KnownSubagentType_ReturnsDefinitionAutonomyLevel()
    {
        // Arrange
        var definition = new SubagentDefinition
        {
            AgentType = SubagentType.Explore,
            AutonomyLevel = AutonomyLevel.Restricted
        };

        _registryMock
            .Setup(r => r.GetProfile(SubagentType.Explore))
            .Returns(definition);

        var resolver = CreateResolver();

        // Act
        var result = resolver.Resolve(SubagentType.Explore);

        // Assert
        result.Should().Be(AutonomyLevel.Restricted);
    }

    [Fact]
    public void Resolve_SubagentDefinition_ReturnsDirectLevel()
    {
        // Arrange
        var definition = new SubagentDefinition
        {
            AgentType = SubagentType.General,
            AutonomyLevel = AutonomyLevel.Autonomous
        };

        var resolver = CreateResolver();

        // Act
        var result = resolver.Resolve(definition);

        // Assert
        result.Should().Be(AutonomyLevel.Autonomous);
    }

    [Fact]
    public void Resolve_SubagentDefinition_DefaultLevel_ReturnsSupervisedDefault()
    {
        // Arrange — SubagentDefinition defaults AutonomyLevel to Supervised
        var definition = new SubagentDefinition
        {
            AgentType = SubagentType.Execute
        };

        var resolver = CreateResolver();

        // Act
        var result = resolver.Resolve(definition);

        // Assert
        result.Should().Be(AutonomyLevel.Supervised);
    }

    [Fact]
    public void Resolve_UnknownType_WhenRegistryThrows_ReturnsFallbackFromConfig()
    {
        // Arrange
        _registryMock
            .Setup(r => r.GetProfile(SubagentType.General))
            .Throws(new KeyNotFoundException("No profile for General"));

        var resolver = CreateResolver(defaultLevel: "Restricted");

        // Act
        var result = resolver.Resolve(SubagentType.General);

        // Assert
        result.Should().Be(AutonomyLevel.Restricted);
    }

    [Theory]
    [InlineData("99")]                          // outside the defined range
    [InlineData(" 99")]                         // and behind a stray space
    [InlineData("2")]                           // the numeric form of Autonomous
    [InlineData("Restricted,Autonomous")]       // comma-composite, OR'd to Autonomous
    public void Resolve_NonNameDefaultAutonomyLevel_FallsBackToSupervisedNotToTheParsedValue(string configured)
    {
        // #300. The config fallback path is the last line of defence when the registry cannot supply
        // a profile, so it must never resolve to a tier nobody wrote. A bare Enum.TryParse accepts
        // every value here — and because the tier ordering runs Restricted < Supervised < Autonomous,
        // "99" reads as looser than the loosest real tier and "2" silently grants full autonomy off a
        // typo. Supervised is the documented fallback; it only applies if the parse can reject.
        _registryMock
            .Setup(r => r.GetProfile(SubagentType.General))
            .Throws(new KeyNotFoundException("No profile for General"));

        var resolver = CreateResolver(defaultLevel: configured);

        resolver.Resolve(SubagentType.General).Should().Be(AutonomyLevel.Supervised);
    }

    [Fact]
    public void Resolve_NamedDefaultAutonomyLevel_IsStillHonoured()
    {
        // The control for the theory above: refusing non-names must not mean ignoring the setting.
        // Resolve_UnknownType_WhenRegistryThrows_ReturnsFallbackFromConfig covers Restricted; this
        // pins the loosest tier explicitly so a regression cannot hide behind Supervised being both
        // the fallback and a plausible answer.
        _registryMock
            .Setup(r => r.GetProfile(SubagentType.General))
            .Throws(new KeyNotFoundException("No profile for General"));

        var resolver = CreateResolver(defaultLevel: nameof(AutonomyLevel.Autonomous));

        resolver.Resolve(SubagentType.General).Should().Be(AutonomyLevel.Autonomous);
    }
}
