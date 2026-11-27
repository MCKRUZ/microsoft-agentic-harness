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
    private const string Fragment = "could not be resolved from keyed DI";

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
        logger.Logged(LogLevel.Warning, "could not be built in this host").Should().BeFalse(
            "a name nobody registered must not be reported as a tool this host failed to build");
    }

    [Fact]
    public async Task BuildToolsAsync_AllowedToolThatCannotBeBuiltInThisHost_IsDroppedRatherThanFailingTheTurn()
    {
        // A tool registered in every host whose factory needs a service only one host provides (the render tools
        // need IClientToolBridge, which only AgentHub registers). Building it elsewhere used to throw out of the
        // agent's turn (#1072); it is unavailable here, the same as a tool that was never registered.
        var logger = new CapturingLogger<ToolChainBuilder>();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("render_image", (_, _) =>
            throw new InvalidOperationException("No service for type 'IClientToolBridge' has been registered."));
        var builder = new ToolChainBuilder(logger, services.BuildServiceProvider());
        var skill = new SkillDefinition { Id = "s", Name = "s", Instructions = "Test", AllowedTools = ["render_image"] };

        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());

        tools.Should().BeEmpty();
        logger.Logged(LogLevel.Warning, "'render_image' is registered but could not be built in this host").Should().BeTrue();
        logger.Logged(LogLevel.Warning, "IClientToolBridge").Should().BeTrue("the warning must say why the tool is missing");
    }

    [Fact]
    public void BuildToolsByName_RegisteredName_DoesNotWarn()
    {
        var logger = new CapturingLogger<ToolChainBuilder>();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("real_tool", Mock.Of<ITool>(t => t.Name == "real_tool"));
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
