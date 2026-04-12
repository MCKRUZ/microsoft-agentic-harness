using Application.AI.Common.Exceptions;
using Application.Core.CQRS.Agents.RunOrchestratedTask;
using Xunit;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.MetaHarness;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// Tests for OrchestratedHarnessProposer JSON extraction and error handling.
/// Uses a mock IMediator that returns scripted agent output strings.
/// </summary>
public class OrchestratedHarnessProposerTests
{
    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly MetaHarnessConfig _config = new();

    private OrchestratedHarnessProposer BuildSut()
    {
        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == _config);
        return new OrchestratedHarnessProposer(
            _mediatorMock.Object,
            opts,
            NullLogger<OrchestratedHarnessProposer>.Instance);
    }

    private static HarnessProposerContext BuildContext() => new()
    {
        CurrentCandidate = new HarnessCandidate
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = Guid.NewGuid(),
            Iteration = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Snapshot = new HarnessSnapshot
            {
                SkillFileSnapshots = new Dictionary<string, string>(),
                SystemPromptSnapshot = "",
                ConfigSnapshot = new Dictionary<string, string>(),
                SnapshotManifest = []
            },
            Status = HarnessCandidateStatus.Evaluated
        },
        OptimizationRunDirectoryPath = Path.GetTempPath(),
        PriorCandidateIds = [],
        Iteration = 1
    };

    private void SetupMediatorResult(string finalSynthesis)
    {
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<RunOrchestratedTaskCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrchestratedTaskResult
            {
                Success = true,
                FinalSynthesis = finalSynthesis,
                SubAgentResults = []
            });
    }

    /// <summary>
    /// When the agent returns a string containing a valid JSON block, ProposeAsync
    /// should extract the first '{' to last '}' substring, parse it, and return a
    /// populated HarnessProposal.
    /// </summary>
    [Fact]
    public async Task ProposeAsync_ValidJsonBlock_ReturnsParsedProposal()
    {
        const string agentOutput = """
            Here is my analysis.
            {
              "reasoning": "Need to improve skill clarity.",
              "proposed_skill_changes": { "skills/harness-proposer/SKILL.md": "# Updated" },
              "proposed_config_changes": { "MetaHarness:MaxIterations": "5" },
              "proposed_system_prompt_change": null
            }
            Let me know if you need more details.
            """;
        SetupMediatorResult(agentOutput);

        var result = await BuildSut().ProposeAsync(BuildContext(), CancellationToken.None);

        Assert.Equal("Need to improve skill clarity.", result.Reasoning);
        Assert.Single(result.ProposedSkillChanges);
        Assert.Equal("# Updated", result.ProposedSkillChanges["skills/harness-proposer/SKILL.md"]);
        Assert.Single(result.ProposedConfigChanges);
        Assert.Equal("5", result.ProposedConfigChanges["MetaHarness:MaxIterations"]);
        Assert.Null(result.ProposedSystemPromptChange);
    }

    /// <summary>
    /// When the agent returns text that contains no valid JSON object (no matching
    /// braces), ProposeAsync should throw HarnessProposalParsingException with the
    /// raw output included in the exception message.
    /// </summary>
    [Fact]
    public async Task ProposeAsync_InvalidJsonOutput_ThrowsHarnessProposalParsingException()
    {
        SetupMediatorResult("No JSON here, sorry.");

        var ex = await Assert.ThrowsAsync<HarnessProposalParsingException>(
            () => BuildSut().ProposeAsync(BuildContext(), CancellationToken.None));

        Assert.Contains("No JSON here, sorry.", ex.RawOutput);
    }

    /// <summary>
    /// When the JSON block is valid but ProposedSkillChanges and ProposedConfigChanges
    /// are absent or empty, ProposeAsync should return a HarnessProposal with empty
    /// dictionaries (not null) and a non-null Reasoning string.
    /// </summary>
    [Fact]
    public async Task ProposeAsync_EmptyProposedChanges_ReturnsProposalWithEmptyDicts()
    {
        SetupMediatorResult("""{"reasoning": "Nothing to change yet."}""");

        var result = await BuildSut().ProposeAsync(BuildContext(), CancellationToken.None);

        Assert.Equal("Nothing to change yet.", result.Reasoning);
        Assert.Empty(result.ProposedSkillChanges);
        Assert.Empty(result.ProposedConfigChanges);
        Assert.Null(result.ProposedSystemPromptChange);
    }

    /// <summary>
    /// When the JSON block includes a "reasoning" field, its value should be surfaced
    /// on HarnessProposal.Reasoning verbatim.
    /// </summary>
    [Fact]
    public async Task ProposeAsync_ProposalContainsReasoning_ReasoningPassedThrough()
    {
        const string reasoning = "The agent missed tool selection on 3 out of 5 tasks.";
        SetupMediatorResult(
            $$$"""{"reasoning": "{{{reasoning}}}", "proposed_skill_changes": {}, "proposed_config_changes": {}}""");

        var result = await BuildSut().ProposeAsync(BuildContext(), CancellationToken.None);

        Assert.Equal(reasoning, result.Reasoning);
    }
}
