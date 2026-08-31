using Application.Core.CQRS.Agents.RunOrchestratedTask;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS;

public class RunOrchestratedTaskCommandValidatorTests
{
    private readonly RunOrchestratedTaskCommandValidator _validator = new();

    private static RunOrchestratedTaskCommand CreateValidCommand() => new()
    {
        OrchestratorName = "OrchestratorAgent",
        TaskDescription = "Analyze the codebase and produce a summary report.",
        AvailableAgents = ["ResearchAgent", "CodeReviewAgent"],
        MaxTotalTurns = 20
    };

    [Fact]
    public async Task Validate_EmptyOrchestratorName_FailsValidation()
    {
        var command = CreateValidCommand() with { OrchestratorName = "" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "OrchestratorName");
    }

    [Fact]
    public async Task Validate_EmptyTaskDescription_FailsValidation()
    {
        var command = CreateValidCommand() with { TaskDescription = "" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "TaskDescription");
    }

    [Fact]
    public async Task Validate_TaskDescriptionOver50KB_FailsValidation()
    {
        var command = CreateValidCommand() with { TaskDescription = new string('x', 50_001) };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e =>
            e.PropertyName == "TaskDescription" &&
            e.ErrorMessage.Contains("maximum length"));
    }

    [Fact]
    public async Task Validate_EmptyAvailableAgents_FailsValidation()
    {
        var command = CreateValidCommand() with { AvailableAgents = [] };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "AvailableAgents");
    }

    [Fact]
    public async Task Validate_MaxTotalTurnsBelowOne_FailsValidation()
    {
        var command = CreateValidCommand() with { MaxTotalTurns = 0 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "MaxTotalTurns");
    }

    [Fact]
    public async Task Validate_MaxTotalTurnsAbove200_FailsValidation()
    {
        var command = CreateValidCommand() with { MaxTotalTurns = 201 };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "MaxTotalTurns");
    }

    [Fact]
    public async Task Validate_ValidCommand_PassesValidation()
    {
        var command = CreateValidCommand();

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // #560: this command's ConversationId flows straight through to the tool-result retrieval
    // scope (RunOrchestratedTaskCommandHandler -> AgentExecutionContext.Initialize) but had zero
    // rules before this fix — previously untested gap, unlike its RunConversationCommand sibling.
    [Fact]
    public async Task Validate_BlankConversationId_Fails()
    {
        // /code-review finding: the ConversationId chain now runs Cascade(Stop), so a blank value
        // fails only NotEmpty — the charset/shape rules never run against it. Asserts "at least one"
        // rather than "exactly one" so this test doesn't depend on that cascade detail either way.
        var command = CreateValidCommand() with { ConversationId = "" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ConversationId");
    }

    [Fact]
    public async Task Validate_NullConversationId_FailsCleanlyRatherThanThrowing()
    {
        // /code-review finding — see RunConversationCommandValidatorTests' identical addition for
        // the full empirically-reproduced NullReferenceException this guards against.
        var command = CreateValidCommand() with { ConversationId = null! };

        var act = () => _validator.ValidateAsync(command);

        await act.Should().NotThrowAsync();
        var result = await act();
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ConversationId");
    }

    [Fact]
    public async Task Validate_ConversationIdWithPathSeparator_Fails()
    {
        var command = CreateValidCommand() with { ConversationId = "../escape" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == "ConversationId");
    }

    [Theory]
    // /code-review finding: each of these clears the AllowedScopeIdCharset regex entirely — see
    // RunConversationCommandValidatorTests' identical addition for the full rationale.
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("C:")]
    [InlineData("conv-1.")]
    public async Task Validate_ConversationIdClearsCharsetButUnsafeShape_Fails(string conversationId)
    {
        var command = CreateValidCommand() with { ConversationId = conversationId };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ConversationId");
    }

    [Fact]
    public async Task Validate_ConversationIdShapedLikeAPlanStep_Passes()
    {
        // Regression: an earlier version of this validator's charset excluded ':' entirely, which
        // rejected PlanRunKeys.StepConversationId's own "{runScope}:{stepId}" shape.
        var command = CreateValidCommand() with { ConversationId = "a1b2c3d4-conv:step-7" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ConversationIdAtTheWorstCasePlanStepLength_Passes()
    {
        // Regression: an earlier version of this validator bounded length at 128 to match
        // IPlanRunExecutor.MaxAgentIdLength, but PlanRunKeys.StepConversationId derives
        // "{runScope}:{stepId}" from that value, up to 128 + 1 + 36 = 165 characters.
        var derivedId = $"{new string('a', 128)}:{Guid.NewGuid()}";
        derivedId.Length.Should().Be(165);

        var command = CreateValidCommand() with { ConversationId = derivedId };
        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_ConversationIdEndingInNewline_Fails()
    {
        // Security-review finding: "$" matches immediately before a trailing '\n' in .NET regex, not
        // only at the true end of the string. Fixed by anchoring with \A/\z instead.
        var command = CreateValidCommand() with { ConversationId = "conv-1\n" };

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
    }
}
