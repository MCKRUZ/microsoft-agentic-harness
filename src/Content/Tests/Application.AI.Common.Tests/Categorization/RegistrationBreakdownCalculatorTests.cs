using Application.AI.Common.Categorization;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Context;
using Domain.AI.Context;
using FluentAssertions;

namespace Application.AI.Common.Tests.Categorization;

/// <summary>
/// Pins <see cref="RegistrationBreakdownCalculator"/> — the measured per-category totals that replaced
/// the residual subtraction behind the Foresight context bar (#507).
/// </summary>
/// <remarks>
/// The property that matters most here is that four lanes which could only ever read zero now carry
/// real numbers. The bar has six segments and, before this, exactly two of them could ever be
/// non-empty: one bucket absorbed everything the transcript estimate failed to explain, and the
/// others had no source at all.
/// </remarks>
public sealed class RegistrationBreakdownCalculatorTests
{
    private static RegistrationSnapshot Snapshot(
        string? systemPrompt = null,
        IReadOnlyList<SkillRegistration>? skills = null,
        IReadOnlyList<ToolRegistration>? nativeTools = null,
        IReadOnlyList<ToolRegistration>? mcpTools = null,
        IReadOnlyList<AgentRegistration>? subAgents = null) =>
        new(systemPrompt, skills ?? [], nativeTools ?? [], mcpTools ?? [], subAgents ?? []);

    private static int Est(string text) => TokenEstimationHelper.EstimateTokens(text);

    [Fact]
    public void From_PopulatedSnapshot_FillsEveryLaneTheBarRenders()
    {
        var skill = new string('s', 200);
        var schema = new string('j', 400);
        var mcpSchema = new string('m', 120);
        var peer = new string('p', 80);
        var instruction = new string('i', 1_000) + skill;

        var breakdown = RegistrationBreakdownCalculator.From(Snapshot(
            systemPrompt: instruction,
            skills: [new SkillRegistration("s1", "Skill One", skill)],
            nativeTools: [new ToolRegistration("read", "Reads", schema)],
            mcpTools: [new ToolRegistration("remote", "Remote", mcpSchema)],
            subAgents: [new AgentRegistration("a1", "Peer", peer)]));

        breakdown.Skills.Should().Be(Est(skill));
        breakdown.Tools.Should().Be(Est(schema));
        breakdown.Mcp.Should().Be(Est(mcpSchema));
        breakdown.Agents.Should().Be(Est(peer));
        breakdown.System.Should().Be(Est(instruction) - Est(skill));

        breakdown.Messages.Should().Be(0,
            "the transcript is measured by DefaultContextSnapshotComputer from history; filling it "
            + "here too would double-count it");
    }

    [Fact]
    public void From_SkillTextEmbeddedInTheInstruction_IsNotChargedTwice()
    {
        // The agent's instruction contains its skills' instructions. Charging both in full would
        // report more context than the model actually receives.
        var skill = new string('s', 400);
        var instruction = "preamble " + skill;

        var breakdown = RegistrationBreakdownCalculator.From(Snapshot(
            systemPrompt: instruction,
            skills: [new SkillRegistration("s1", "Skill", skill)]));

        (breakdown.System + breakdown.Skills).Should().Be(Est(instruction),
            "the two lanes together must add up to the instruction actually sent");
    }

    [Fact]
    public void From_ToolWithoutASerializedSchema_StillCostsSomething()
    {
        // A non-AIFunction tool has no schema text but still occupies context. Reporting zero would
        // understate the lane and quietly widen the unaccounted gap.
        var breakdown = RegistrationBreakdownCalculator.From(Snapshot(
            nativeTools: [new ToolRegistration("calculator", "Does arithmetic", SchemaText: null)]));

        breakdown.Tools.Should().Be(Est("calculator Does arithmetic"));
        breakdown.Tools.Should().BeGreaterThan(0);
    }

    [Fact]
    public void From_NativeAndMcpTools_AreCountedInSeparateLanes()
    {
        var nativeSchema = new string('n', 400);
        var mcpSchema = new string('m', 800);

        var breakdown = RegistrationBreakdownCalculator.From(Snapshot(
            nativeTools: [new ToolRegistration("local", null, nativeSchema)],
            mcpTools: [new ToolRegistration("remote", null, mcpSchema)]));

        breakdown.Tools.Should().Be(Est(nativeSchema));
        breakdown.Mcp.Should().Be(Est(mcpSchema),
            "an MCP surface is a different lane from a first-party tool — collapsing them would hide "
            + "which one is consuming the window");
    }

    [Fact]
    public void From_NoSystemPrompt_ReportsZeroRatherThanAnInventedFigure()
    {
        var breakdown = RegistrationBreakdownCalculator.From(Snapshot(
            systemPrompt: null,
            skills: [new SkillRegistration("s1", "Skill", new string('s', 400))]));

        breakdown.System.Should().Be(0);
        breakdown.Skills.Should().Be(Est(new string('s', 400)));
    }

    [Fact]
    public void From_SkillsLargerThanTheInstruction_ClampsSystemWithoutGoingNegative()
    {
        // Only reachable when a registered skill's text is not actually embedded in the instruction.
        // The clamp keeps the lane sane; ContextSnapshot.UnaccountedTokens is where the inconsistency
        // shows up rather than being silently absorbed.
        var breakdown = RegistrationBreakdownCalculator.From(Snapshot(
            systemPrompt: "tiny",
            skills: [new SkillRegistration("s1", "Skill", new string('s', 4_000))]));

        breakdown.System.Should().Be(0);
        breakdown.Total.Should().BeGreaterThan(0);
    }

    [Fact]
    public void From_EmptySnapshot_IsAllZero()
    {
        RegistrationBreakdownCalculator.From(Snapshot()).Should().Be(CategoryBreakdown.Empty);
    }

    [Fact]
    public void From_Null_Throws()
    {
        Action act = () => RegistrationBreakdownCalculator.From(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
