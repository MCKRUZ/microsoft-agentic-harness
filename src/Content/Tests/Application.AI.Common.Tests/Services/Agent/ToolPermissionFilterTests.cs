using Application.AI.Common.Services.Agent;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Agent;

/// <summary>
/// Tests <see cref="ToolPermissionFilter"/> through <see cref="AIContextProvider.InvokingAsync"/> — the
/// public entry point <c>ChatClientAgent</c> uses when it chains context providers.
/// </summary>
/// <remarks>
/// Driving the protected <c>ProvideAIContextAsync</c> directly instead would make every assertion here
/// meaningless. That hook is contractually additive: the base implementation merges whatever it returns
/// into the incoming context as <c>input.Tools.Concat(provided.Tools)</c>, so a filter that answers there
/// with a subset has its removals undone before the model ever sees the tool list. Only the merge itself
/// can remove a tool, which is why the filter overrides <c>InvokingCoreAsync</c> and why these tests must
/// go through the public method to have any force.
/// </remarks>
public sealed class ToolPermissionFilterTests
{
    private static AITool MakeTool(string name)
    {
        var mock = new Mock<AITool>();
        mock.Setup(t => t.Name).Returns(name);
        return mock.Object;
    }

    // The filter reads only the accumulated AIContext — Agent and Session are unused by the SUT.
    private static AIContextProvider.InvokingContext MakeContext(AIContext aiContext) =>
        new(new Mock<AIAgent>().Object, new Mock<AgentSession>().Object, aiContext);

    private static Task<AIContext> Run(IEnumerable<string>? allowedTools, params AITool[] tools) =>
        new ToolPermissionFilter(allowedTools)
            .InvokingAsync(MakeContext(new AIContext { Tools = [.. tools] }))
            .AsTask();

    private static IEnumerable<string> NamesOf(AIContext context) =>
        context.Tools?.Select(t => t.Name) ?? [];

    // ── null allow-list = no restriction ─────────────────────────────────────

    [Fact]
    public async Task NullAllowList_NoTools_YieldsNoTools()
    {
        var result = await Run(null);

        NamesOf(result).Should().BeEmpty();
    }

    [Fact]
    public async Task NullAllowList_WithTools_AllToolsPassThrough()
    {
        var result = await Run(null, MakeTool("Read"), MakeTool("Write"));

        NamesOf(result).Should().BeEquivalentTo(["Read", "Write"]);
    }

    // ── empty (non-null) allow-list = deny all ───────────────────────────────

    [Fact]
    public async Task EmptyAllowList_WithTools_StripsEveryTool()
    {
        // An empty but non-null allow-list is an active restriction that permits nothing — this is the
        // deny-all state a tool ceiling collapses to when it is disjoint from the skills' tools.
        var result = await Run([], MakeTool("Read"), MakeTool("Write"));

        NamesOf(result).Should().BeEmpty();
    }

    // ── filtering behavior ───────────────────────────────────────────────────

    [Fact]
    public async Task AllowedTool_IsRetained()
    {
        var result = await Run(["Read"], MakeTool("Read"));

        NamesOf(result).Should().ContainSingle().Which.Should().Be("Read");
    }

    [Fact]
    public async Task DisallowedTool_IsStripped()
    {
        var result = await Run(["Read"], MakeTool("Write"));

        NamesOf(result).Should().BeEmpty();
    }

    [Fact]
    public async Task MixedTools_OnlyAllowedToolsRetained()
    {
        var result = await Run(
            ["Read", "Search"],
            MakeTool("Read"), MakeTool("Write"), MakeTool("Search"), MakeTool("Delete"));

        NamesOf(result).Should().BeEquivalentTo(["Read", "Search"]);
    }

    [Fact]
    public async Task AllToolsAllowed_ToolListIsUnchanged()
    {
        var result = await Run(["Read", "Write"], MakeTool("Read"), MakeTool("Write"));

        NamesOf(result).Should().BeEquivalentTo(["Read", "Write"]);
    }

    [Fact]
    public async Task PermittedTool_IsNotDuplicated()
    {
        // Guards the merge specifically: the base concatenates the provider's contribution onto the input,
        // so a filter that returned its kept subset additively would emit each allowed tool twice.
        var result = await Run(["Read"], MakeTool("Read"), MakeTool("Write"));

        NamesOf(result).Should().ContainSingle("the allowed tool must appear exactly once");
    }

    // ── framework skill tools are exempt ─────────────────────────────────────

    [Fact]
    public async Task ReadOnlySkillTools_SurviveAnAllowListThatOmitsThem()
    {
        // No skill manifest lists the framework's own disclosure tools in its allowed-tools, so filtering
        // them would switch progressive disclosure off for every agent that declares tool restrictions.
        var result = await Run(
            ["file_system"],
            MakeTool(AgentSkillsProvider.LoadSkillToolName),
            MakeTool(AgentSkillsProvider.ReadSkillResourceToolName));

        NamesOf(result).Should().BeEquivalentTo(
            [AgentSkillsProvider.LoadSkillToolName, AgentSkillsProvider.ReadSkillResourceToolName]);
    }

    [Fact]
    public async Task RunSkillScriptTool_IsNotExempt()
    {
        // The one skill tool that executes something stays under the allow-list, matching the framework's
        // own read-only/all split in AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule.
        var result = await Run(["file_system"], MakeTool(AgentSkillsProvider.RunSkillScriptToolName));

        NamesOf(result).Should().BeEmpty();
    }

    [Fact]
    public async Task SkillToolExemption_DoesNotApplyWhenNoRestrictionIsActive()
    {
        // A null allow-list already permits everything; the exemption must not be the reason.
        var result = await Run(null, MakeTool(AgentSkillsProvider.RunSkillScriptToolName));

        NamesOf(result).Should().ContainSingle();
    }

    // ── case insensitivity ───────────────────────────────────────────────────

    [Fact]
    public async Task ToolNameMatching_IsCaseInsensitive()
    {
        var result = await Run(["read"], MakeTool("READ"));

        NamesOf(result).Should().ContainSingle();
    }

    // ── null / empty tools in context ────────────────────────────────────────

    [Fact]
    public async Task NullToolsInContext_YieldsNoTools()
    {
        var filter = new ToolPermissionFilter(["Read"]);

        var result = await filter.InvokingAsync(MakeContext(new AIContext()));

        NamesOf(result).Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyToolsInContext_YieldsNoTools()
    {
        var result = await Run(["Read"]);

        NamesOf(result).Should().BeEmpty();
    }

    // ── the accumulated context is preserved ─────────────────────────────────

    [Fact]
    public async Task Filtering_PreservesInstructionsFromEarlierProviders()
    {
        // The filter sits mid-chain: dropping a tool must not drop the instructions the skills provider
        // contributed ahead of it.
        var filter = new ToolPermissionFilter(["Read"]);
        var input = new AIContext
        {
            Instructions = "<skill><name>demo</name></skill>",
            Tools = [MakeTool("Read"), MakeTool("Write")]
        };

        var result = await filter.InvokingAsync(MakeContext(input));

        result.Instructions.Should().Be("<skill><name>demo</name></skill>");
        NamesOf(result).Should().ContainSingle().Which.Should().Be("Read");
    }

    // ── the observable allow-list ────────────────────────────────────────────

    [Fact]
    public void AllowedTools_ExposesTheEffectiveRestriction()
    {
        new ToolPermissionFilter(["Read", "Write"]).AllowedTools
            .Should().BeEquivalentTo(["Read", "Write"]);
    }

    [Fact]
    public void AllowedTools_IsNullWhenUnrestricted()
    {
        new ToolPermissionFilter(null).AllowedTools.Should().BeNull();
    }
}
