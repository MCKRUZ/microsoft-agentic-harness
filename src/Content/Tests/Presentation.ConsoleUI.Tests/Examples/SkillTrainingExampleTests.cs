using Application.AI.Common.CQRS.SkillTraining.TrainSkill;
using Domain.AI.SkillTraining;
using FluentAssertions;
using Presentation.ConsoleUI.Examples;
using Xunit;

namespace Presentation.ConsoleUI.Tests.Examples;

/// <summary>
/// Regression coverage for #533: <see cref="SkillTrainingExample"/> calls
/// <see cref="TrainSkillCommandHandler"/> directly instead of through <c>IMediator.Send</c>, so it
/// must validate <see cref="TrainSkillCommand"/> itself before dispatching.
/// </summary>
public sealed class SkillTrainingExampleTests
{
    /// <summary>
    /// A null handler proves the short-circuit: if an invalid command ever reached
    /// <c>handler.Handle</c>, this would throw <see cref="NullReferenceException"/> instead of
    /// returning a validation failure.
    /// </summary>
    [Fact]
    public async Task ValidateAndDispatchAsync_InvalidCommand_ReturnsValidationFailureWithoutInvokingHandler()
    {
        var invalid = new TrainSkillCommand
        {
            RunId = "",
            SkillId = "demo-skill",
            InitialSkill = "# Demo",
            Config = new TrainSkillConfig
            {
                Epochs = 2,
                StepsPerEpoch = 3,
                LrStart = 1,
                LrMin = 4,
                LrScheduler = "bogus",
                GateMetric = GateMetric.Hard,
                Patience = 3,
                Seed = 42
            }
        };

        var result = await SkillTrainingExample.ValidateAndDispatchAsync(
            handler: null!, invalid, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("RunId"));
        result.Errors.Should().Contain(e => e.Contains("LrScheduler"));
        result.Errors.Should().Contain(e => e.Contains("LrMin"));
    }

    /// <summary>
    /// The demo's own hardcoded command must keep passing validation, or the demo would silently
    /// stop running.
    /// </summary>
    [Fact]
    public async Task ValidateAndDispatchAsync_DemoCommand_PassesValidation()
    {
        var demoCommand = new TrainSkillCommand
        {
            RunId = "demo-run-1",
            SkillId = "demo-skill",
            InitialSkill = "# Demo Skill\n\n## Approach\n- Start simple.",
            Config = new TrainSkillConfig
            {
                Epochs = 2,
                StepsPerEpoch = 3,
                LrStart = 4,
                LrMin = 1,
                LrScheduler = "cosine",
                GateMetric = GateMetric.Hard,
                Patience = 3,
                UseSlowUpdate = false,
                UseMetaSkill = false,
                Seed = 42
            }
        };

        var validator = new TrainSkillCommandValidator();
        var validationResult = await validator.ValidateAsync(demoCommand);

        validationResult.IsValid.Should().BeTrue();
    }
}
