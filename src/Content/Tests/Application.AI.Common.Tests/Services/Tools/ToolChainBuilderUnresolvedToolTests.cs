using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Skills;
using Domain.AI.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Tests.Common;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Pins #840: a tool name that no keyed-DI registration answers to is dropped from the chain, and the
/// drop is logged where it is final. Before, a skill that declared a tool which does not exist ran
/// without it and left no trace of why. The warning IS the behaviour under test: the tool set is
/// unchanged. It is deliberately not emitted where a miss is a normal step (an optional declaration),
/// which would be a false alarm on every build of a correctly configured skill.
/// </summary>
public sealed class ToolChainBuilderUnresolvedToolTests
{
    private const string Fragment = "is not registered under that key";

    [Fact]
    public void BuildToolsByName_NameWithNoRegistration_IsDroppedAndWarnedAbout()
    {
        var logger = new CapturingLogger<ToolChainBuilder>();
        var builder = new ToolChainBuilder(logger, new ServiceCollection().BuildServiceProvider());

        var tools = builder.BuildToolsByName(["no_such_tool"]);

        tools.Should().BeEmpty();
        logger.Logged(LogLevel.Warning, "'no_such_tool' " + Fragment).Should().BeTrue();
    }

    [Fact]
    public async Task BuildToolsAsync_AllowedToolWithNoRegistration_IsDroppedAndWarnedAbout()
    {
        var logger = new CapturingLogger<ToolChainBuilder>();
        var builder = new ToolChainBuilder(logger, new ServiceCollection().BuildServiceProvider());
        var skill = new SkillDefinition { Id = "s", Name = "s", Instructions = "Test", AllowedTools = ["no_such_tool"] };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().BeEmpty();
        logger.Logged(LogLevel.Warning, "'no_such_tool' " + Fragment).Should().BeTrue();
    }

    [Fact]
    public void BuildToolsByName_RegisteredName_DoesNotWarn()
    {
        var logger = new CapturingLogger<ToolChainBuilder>();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("real_tool", Mock.Of<ITool>());
        var builder = new ToolChainBuilder(logger, services.BuildServiceProvider());

        builder.BuildToolsByName(["real_tool"]);

        logger.Logged(LogLevel.Warning, Fragment).Should().BeFalse();
    }

    [Fact]
    public async Task BuildToolsAsync_OptionalDeclarationWithNoRegistration_DoesNotWarn()
    {
        var logger = new CapturingLogger<ToolChainBuilder>();
        var builder = new ToolChainBuilder(logger, new ServiceCollection().BuildServiceProvider());
        var skill = new SkillDefinition
        {
            Id = "s", Name = "s", Instructions = "Test",
            ToolDeclarations = [new ToolDeclaration { Name = "optional", Optional = true }]
        };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().BeEmpty();
        logger.Logged(LogLevel.Warning, Fragment).Should().BeFalse(
            "an optional tool that is absent is a normal outcome, not a dropped declaration");
    }
}
