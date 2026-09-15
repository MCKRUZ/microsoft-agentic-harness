using Application.AI.Common.Services.Governance;
using Domain.AI.Agents;
using Infrastructure.AI.Tests.Helpers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// Tests for #618's remaining gap: an eval candidate's own <c>egress:</c> allowlist must actually
/// reach <see cref="Infrastructure.AI.Egress.SkillManifestEgressPolicyResolver"/> during the agent run, not just get parsed.
/// Split from <see cref="AgentEvaluationServiceTests"/> and its SkillMaterialization partial per
/// this project's Partial Class Pattern.
/// </summary>
public partial class AgentEvaluationServiceTests
{
    private static readonly Dictionary<string, string> SkillWithEgressAllowlist = new()
    {
        ["SKILL.md"] =
            "---\n" +
            "name: research-agent\n" +
            "description: Finds and analyzes information.\n" +
            "egress:\n" +
            "  allowlist:\n" +
            "    - host: api.example.com\n" +
            "      schemes: [https]\n" +
            "      ports: [443]\n" +
            "---\n" +
            "# Research Agent\nDo research.\n"
    };

    /// <summary>
    /// The ephemeral scope must be open for exactly the span of <c>agent.RunAsync</c> — proved by
    /// observing <see cref="EphemeralSkillMetadataAccessor.TryGet"/> from INSIDE the fake agent's own
    /// run handler, the same async flow the real <c>GovernedAIFunction</c> would consult it from.
    /// Before #618, nothing published anything here — this fails on main without the fix.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_CandidateWithEgressAllowlist_PublishesEphemeralSkillDuringRunAsync()
    {
        Domain.AI.Skills.SkillDefinition? observedDuringRun = null;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent((_, _) =>
            {
                observedDuringRun = EphemeralSkillMetadataAccessor.TryGet("research-agent");
                return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "output")));
            }));

        var sut = BuildSut();
        var candidate = BuildCandidate(skillFiles: SkillWithEgressAllowlist);
        var tasks = new[] { BuildTask("egress-task", "prompt", pattern: null) };

        await sut.EvaluateAsync(candidate, tasks);

        Assert.NotNull(observedDuringRun);
        Assert.Equal("research-agent", observedDuringRun!.Id);
        Assert.Equal("api.example.com", observedDuringRun.Egress?.Allowlist.Single().Host);

        // Torn down once the run completes — a later, unrelated resolution must not still see it.
        Assert.Null(EphemeralSkillMetadataAccessor.TryGet("research-agent"));
    }

    /// <summary>
    /// #618 scoped this fix to the common single-skill case (see
    /// <see cref="EphemeralSkillMetadataAccessor"/>'s remarks). A candidate with NO skill files at
    /// all must not attempt to build a candidate skill definition — proving the null-skillDirectory
    /// path composes cleanly with the new wiring rather than throwing.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_CandidateWithNoSkillFiles_RunsWithoutEphemeralScope()
    {
        Domain.AI.Skills.SkillDefinition? observedDuringRun = null;
        var sawEphemeralLookup = false;
        _agentFactoryMock
            .Setup(f => f.CreateAgentAsync(It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestableAIAgent((_, _) =>
            {
                sawEphemeralLookup = true;
                observedDuringRun = EphemeralSkillMetadataAccessor.TryGet("anything");
                return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "output")));
            }));

        var sut = BuildSut();
        var candidate = BuildCandidate(); // empty SkillFileSnapshots
        var tasks = new[] { BuildTask("no-skill-task", "prompt", pattern: null) };

        var result = await sut.EvaluateAsync(candidate, tasks);

        Assert.True(sawEphemeralLookup);
        Assert.Null(observedDuringRun);
        Assert.Equal(1.0, result.PassRate);
    }
}
