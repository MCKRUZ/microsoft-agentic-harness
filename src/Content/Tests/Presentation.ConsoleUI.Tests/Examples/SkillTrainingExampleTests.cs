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
    /// Exercises ValidateAndDispatchAsync's success path end-to-end: the demo's own command
    /// (via <see cref="SkillTrainingExample.BuildDemoCommand"/>, not a separately-typed copy)
    /// against a real handler (via <see cref="SkillTrainingExample.BuildHandler"/>) must pass
    /// validation and reach handler.Handle, or the demo would silently stop running.
    /// </summary>
    [Fact]
    public async Task ValidateAndDispatchAsync_DemoCommand_PassesValidationAndDispatchesToHandler()
    {
        var handler = SkillTrainingExample.BuildHandler();
        var demoCommand = SkillTrainingExample.BuildDemoCommand();

        var result = await SkillTrainingExample.ValidateAndDispatchAsync(
            handler, demoCommand, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
    }
}
