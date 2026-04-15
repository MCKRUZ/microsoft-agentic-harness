using Application.Core.CQRS.Agents.RunOrchestratedTask;
using Domain.Common.Config.MetaHarness;
using Domain.Common.MetaHarness;
using Infrastructure.AI.MetaHarness;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.MetaHarness;

/// <summary>
/// Tests for learnings injection and parsing in <see cref="OrchestratedHarnessProposer"/>.
/// </summary>
public class OrchestratedHarnessProposer_LearningsTests
{
    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly MetaHarnessConfig _config = new();

    private OrchestratedHarnessProposer BuildSut()
    {
        var opts = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == _config);
        return new OrchestratedHarnessProposer(
            _mediatorMock.Object, opts, NullLogger<OrchestratedHarnessProposer>.Instance);
    }

    private static HarnessProposerContext BuildContext(string? priorLearnings = null) => new()
    {
        CurrentCandidate = new HarnessCandidate
        {
            CandidateId = Guid.NewGuid(),
            OptimizationRunId = Guid.NewGuid(),
            Iteration = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = HarnessCandidateStatus.Proposed,
            Snapshot = new HarnessSnapshot
            {
                SkillFileSnapshots = new Dictionary<string, string>(),
                SystemPromptSnapshot = "prompt",
                ConfigSnapshot = new Dictionary<string, string>(),
                SnapshotManifest = [],
            },
        },
        OptimizationRunDirectoryPath = "/opt/run",
        PriorCandidateIds = [],
        Iteration = 1,
        PriorLearnings = priorLearnings,
    };

    private static OrchestratedTaskResult MakeResult(string jsonOutput) => new()
    {
        Success = true,
        FinalSynthesis = jsonOutput,
        SubAgentResults = [],
    };

    private void SetupMediator(string jsonOutput)
    {
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<RunOrchestratedTaskCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult(jsonOutput));
    }

    [Fact]
    public async Task ProposeAsync_WithPriorLearnings_IncludesInPrompt()
    {
        // Arrange
        RunOrchestratedTaskCommand? capturedCommand = null;
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<RunOrchestratedTaskCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<OrchestratedTaskResult>, CancellationToken>((cmd, _) =>
                capturedCommand = (RunOrchestratedTaskCommand)cmd)
            .ReturnsAsync(MakeResult("""{"reasoning":"r","proposed_skill_changes":{},"proposed_config_changes":{}}"""));

        var ctx = BuildContext(priorLearnings: "Pattern: prompt clarity matters");
        var sut = BuildSut();

        // Act
        await sut.ProposeAsync(ctx, default);

        // Assert: task description includes the learnings text
        Assert.NotNull(capturedCommand);
        Assert.Contains("Pattern: prompt clarity matters", capturedCommand.TaskDescription);
        Assert.Contains("Prior Learnings", capturedCommand.TaskDescription);
    }

    [Fact]
    public async Task ProposeAsync_NullPriorLearnings_OmitsLearningsSection()
    {
        // Arrange
        RunOrchestratedTaskCommand? capturedCommand = null;
        _mediatorMock
            .Setup(m => m.Send(It.IsAny<RunOrchestratedTaskCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<OrchestratedTaskResult>, CancellationToken>((cmd, _) =>
                capturedCommand = (RunOrchestratedTaskCommand)cmd)
            .ReturnsAsync(MakeResult("""{"reasoning":"r","proposed_skill_changes":{},"proposed_config_changes":{}}"""));

        var ctx = BuildContext(priorLearnings: null);
        var sut = BuildSut();

        // Act
        await sut.ProposeAsync(ctx, default);

        // Assert: no learnings section in prompt
        Assert.NotNull(capturedCommand);
        Assert.DoesNotContain("Prior Learnings", capturedCommand.TaskDescription);
    }

    [Fact]
    public async Task ProposeAsync_OutputContainsLearnings_ParsedIntoProposal()
    {
        // Arrange
        SetupMediator("""
            {
              "reasoning": "test",
              "proposed_skill_changes": {},
              "proposed_config_changes": {},
              "learnings": "Tool calls are rate limited; batch when possible"
            }
            """);

        var sut = BuildSut();

        // Act
        var proposal = await sut.ProposeAsync(BuildContext(), default);

        // Assert
        Assert.Equal("Tool calls are rate limited; batch when possible", proposal.Learnings);
    }

    [Fact]
    public async Task ProposeAsync_OutputMissingLearnings_LearningsIsNull()
    {
        // Arrange
        SetupMediator("""{"reasoning":"r","proposed_skill_changes":{},"proposed_config_changes":{}}""");

        var sut = BuildSut();

        // Act
        var proposal = await sut.ProposeAsync(BuildContext(), default);

        // Assert
        Assert.Null(proposal.Learnings);
    }
}
