using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Tools;
using Domain.AI.Governance;
using Domain.AI.Skills;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// #531: proves the missing wire — <see cref="ToolChainBuilder"/> and <see cref="GovernedAIFunction"/>
/// actually establish <see cref="ICurrentSkillAccessor.CurrentSkillId"/> for the duration of a tool
/// call built from a skill, which is the one thing standing between
/// <c>SkillManifestEgressPolicyResolver</c>'s already-correct merge logic and it ever running in
/// production. Uses the real <see cref="CurrentSkillAccessor"/> (Infrastructure), not a mock — the
/// mechanism under test IS the AsyncLocal flow, so a fake accessor would prove nothing.
/// </summary>
public sealed class SkillEgressScopeActivationTests
{
    private static (ITool Tool, Mock<IFileSystemService> FileSystem, ICurrentSkillAccessor Accessor, IServiceProvider Provider)
        BuildFixture()
    {
        var fileSystem = new Mock<IFileSystemService>();
        var tool = new FileSystemTool(fileSystem.Object);
        var accessor = new CurrentSkillAccessor();

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("file_system", tool);
        services.AddSingleton<ICurrentSkillAccessor>(accessor);

        return (tool, fileSystem, accessor, services.BuildServiceProvider());
    }

    [Fact]
    public async Task ToolBuiltFromSkill_EstablishesCurrentSkillId_ForTheDurationOfTheCall()
    {
        var (_, fileSystem, accessor, provider) = BuildFixture();

        string? observedDuringCall = null;
        fileSystem
            .Setup(fs => fs.ReadFileAsync("notes.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                observedDuringCall = accessor.CurrentSkillId;
                return "hello world";
            });

        var builder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance, provider, new AIToolConverter(NullLogger<AIToolConverter>.Instance));

        var skill = new SkillDefinition { Id = "reader-skill", Name = "reader-skill", AllowedTools = ["file_system"] };
        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());
        var aiFunction = (AIFunction)tools.Single();

        accessor.CurrentSkillId.Should().BeNull("no skill scope should be active before the call");

        var args = new AIFunctionArguments
        {
            ["operation"] = "read",
            ["parametersJson"] = System.Text.Json.JsonSerializer.SerializeToElement(new { path = "notes.txt" })
        };
        await aiFunction.InvokeAsync(args);

        observedDuringCall.Should().Be("reader-skill", "the resolver reads CurrentSkillId from inside the tool's own execution");
        accessor.CurrentSkillId.Should().BeNull("the scope must be restored once the call returns, not leaked to the next call");
    }

    [Fact]
    public async Task ToolBuiltWithoutASkill_BuildToolsByName_NeverEstablishesASkillScope()
    {
        // BuildToolsByName (delegated subagents) has no skill context — this must stay a documented
        // no-op, not silently inherit whatever happens to be ambient from an unrelated caller.
        var (_, fileSystem, accessor, provider) = BuildFixture();

        string? observedDuringCall = "not-yet-observed";
        fileSystem
            .Setup(fs => fs.ReadFileAsync("notes.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                observedDuringCall = accessor.CurrentSkillId;
                return "hello world";
            });

        var builder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance, provider, new AIToolConverter(NullLogger<AIToolConverter>.Instance));
        var aiFunction = (AIFunction)builder.BuildToolsByName(["file_system"], "test-agent").Single();

        var args = new AIFunctionArguments
        {
            ["operation"] = "read",
            ["parametersJson"] = System.Text.Json.JsonSerializer.SerializeToElement(new { path = "notes.txt" })
        };
        await aiFunction.InvokeAsync(args);

        observedDuringCall.Should().BeNull();
    }

    /// <summary>Always reports one finding naming <c>file_system</c> as the sink, forcing
    /// <c>ToolChainBuilder.ApplyCompositionTaint</c> to re-wrap it.</summary>
    private sealed class AlwaysFindsCompositionAnalyzer : IToolCompositionAnalyzer
    {
        public ToolCompositionAssessment Analyze(IReadOnlyList<AITool> tools) => new(
            [new ToolCompositionFinding(
                "some_source_tool", ToolCompositionCapability.IngestsUntrustedInput,
                "file_system", ToolCompositionCapability.WritesFiles,
                ToolCapabilityOrigin.FirstParty, ToolCapabilityOrigin.FirstParty)],
            []);
    }

    [Fact]
    public async Task CompositionTaintRewrap_CarriesTheSkillScopeForward()
    {
        // Regression for the exact "second caller of a fixed pattern forgets the fix" trap this repo's
        // CLAUDE.md records against PR #545: ToolChainBuilder.ApplyCompositionTaint re-wraps a
        // GovernedAIFunction when a composition finding implicates it — drives the REAL rewrap path
        // (not a hand-rolled restatement of what it should do), since a unit-style test that just
        // re-implements the expected shape cannot catch a regression in the rewrap itself. A prior
        // version of this test did exactly that and was proven blind by mutation-testing the rewrap.
        var (_, fileSystem, accessor, provider) = BuildFixture();

        string? observedDuringCall = null;
        fileSystem
            .Setup(fs => fs.ReadFileAsync("notes.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                observedDuringCall = accessor.CurrentSkillId;
                return "hello world";
            });

        var builder = new ToolChainBuilder(
            NullLogger<ToolChainBuilder>.Instance, provider, new AIToolConverter(NullLogger<AIToolConverter>.Instance),
            compositionAnalyzer: new AlwaysFindsCompositionAnalyzer());

        var skill = new SkillDefinition { Id = "reader-skill", Name = "reader-skill", AllowedTools = ["file_system"] };
        var tools = await builder.BuildToolsAsync(skill, new SkillAgentOptions());
        var aiFunction = (AIFunction)tools.Single();

        var args = new AIFunctionArguments
        {
            ["operation"] = "read",
            ["parametersJson"] = System.Text.Json.JsonSerializer.SerializeToElement(new { path = "notes.txt" })
        };
        await aiFunction.InvokeAsync(args);

        observedDuringCall.Should().Be(
            "reader-skill", "the re-wrapped instance must still carry the skill scope forward");
    }
}
