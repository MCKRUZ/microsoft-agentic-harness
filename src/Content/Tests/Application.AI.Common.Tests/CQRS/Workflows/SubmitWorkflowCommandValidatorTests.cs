using Application.AI.Common.CQRS.Workflows.Submit;
using Domain.AI.Escalation;
using Domain.AI.Planner;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Workflows;

/// <summary>
/// Tests for <see cref="SubmitWorkflowCommandValidator"/>: the admission caps and the wire-level
/// integrity rules that the domain model cannot express.
/// </summary>
/// <remarks>
/// The ceiling cases matter most. Every one of them protects a cost the graph-size caps do not bound —
/// a two-step workflow can still ask for an unlimited budget, unlimited parallelism, or unlimited
/// retries — and each is asserted to <em>reject</em> rather than quietly lower the request.
/// </remarks>
public sealed class SubmitWorkflowCommandValidatorTests
{
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private readonly AIConfig _config = new();

    private SubmitWorkflowCommandValidator Sut => new(new StaticOptionsMonitor<AIConfig>(_config));

    private static WorkflowStep LlmStep(string name) => new()
    {
        Name = name,
        Type = StepType.LlmCall,
        Configuration = new LlmCallStepConfiguration { SystemPrompt = "p", ModelDeploymentKey = "k" }
    };

    private static SubmitWorkflowCommand Command(
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyList<WorkflowEdge>? edges = null,
        WorkflowExecutionSettings? configuration = null) => new()
        {
            Definition = new WorkflowDefinition
            {
                Name = "wf",
                Steps = steps,
                Edges = edges ?? [],
                Configuration = configuration
            }
        };

    [Fact]
    public void Validate_MinimalWorkflow_IsAccepted()
    {
        Sut.Validate(Command([LlmStep("only")])).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_DuplicateStepNames_AreRejected()
    {
        // Edges refer to steps by name, so duplicates make every edge touching them ambiguous.
        var result = Sut.Validate(Command([LlmStep("same"), LlmStep("same")]));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorMessage.Contains("unique"));
    }

    [Fact]
    public void Validate_EdgeNamingAnUnknownStep_IsRejected()
    {
        var result = Sut.Validate(Command(
            [LlmStep("a")],
            [new WorkflowEdge { From = "a", To = "ghost", Type = EdgeType.ControlFlow }]));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_StepTypeDisagreeingWithItsConfiguration_IsRejected()
    {
        // The discriminator and the Type property state the same fact twice. Preferring either one
        // silently would run a step the caller did not describe.
        var mislabelled = new WorkflowStep
        {
            Name = "confused",
            Type = StepType.HumanGate,
            Configuration = new ToolUseStepConfiguration { ToolName = "file_system" }
        };

        var result = Sut.Validate(Command([mislabelled]));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("different type"));
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(2, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(2, 2, true)]
    public void Validate_ConditionalBranchArms_MustBeExactlyOneEach(
        int trueArms, int falseArms, bool expectRejected)
    {
        var targets = Enumerable.Range(0, trueArms + falseArms).Select(i => LlmStep($"t{i}")).ToList();
        var branch = new WorkflowStep
        {
            Name = "branch",
            Type = StepType.ConditionalBranch,
            Configuration = new ConditionalBranchStepConfiguration { ConditionExpression = "x" }
        };

        var edges = targets
            .Select((t, i) => new WorkflowEdge
            {
                From = "branch",
                To = t.Name,
                Type = i < trueArms ? EdgeType.ConditionalTrue : EdgeType.ConditionalFalse
            })
            .ToList();

        var result = Sut.Validate(Command([branch, .. targets], edges));

        result.IsValid.Should().Be(!expectRejected);
    }

    [Fact]
    public void Validate_PlanTimeoutAboveTheCeiling_IsRejectedNotClamped()
    {
        _config.WorkflowSubmission.MaxPlanTimeout = TimeSpan.FromMinutes(10);

        var command = Command(
            [LlmStep("only")],
            configuration: new WorkflowExecutionSettings { PlanTimeout = TimeSpan.FromMinutes(11) });

        var result = Sut.Validate(command);

        result.IsValid.Should().BeFalse();
        // Rejection is the point: the caller keeps the definition it authored, rather than receiving a
        // silently shortened run whose difference only shows up as a production timeout.
        command.Definition.Configuration!.PlanTimeout.Should().Be(TimeSpan.FromMinutes(11));
    }

    [Fact]
    public void Validate_PlanTimeoutAtTheCeiling_IsAccepted()
    {
        _config.WorkflowSubmission.MaxPlanTimeout = TimeSpan.FromMinutes(10);

        var result = Sut.Validate(Command(
            [LlmStep("only")],
            configuration: new WorkflowExecutionSettings { PlanTimeout = TimeSpan.FromMinutes(10) }));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveParallelism_IsRejected(int requested)
    {
        var result = Sut.Validate(Command(
            [LlmStep("only")],
            configuration: new WorkflowExecutionSettings { MaxParallelSteps = requested }));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ParallelismAboveTheCeiling_IsRejected()
    {
        _config.WorkflowSubmission.MaxParallelSteps = 4;

        var result = Sut.Validate(Command(
            [LlmStep("only")],
            configuration: new WorkflowExecutionSettings { MaxParallelSteps = 5 }));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_StepTimeoutAboveTheCeiling_IsRejected()
    {
        _config.WorkflowSubmission.MaxStepTimeout = TimeSpan.FromMinutes(2);
        var step = LlmStep("slow") with { Timeout = TimeSpan.FromMinutes(3) };

        Sut.Validate(Command([step])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RetriesAboveTheCeiling_AreRejected()
    {
        _config.WorkflowSubmission.MaxRetriesPerStep = 2;
        var step = LlmStep("flaky") with { Retry = new WorkflowRetrySettings { MaxRetries = 3 } };

        Sut.Validate(Command([step])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_NegativeRetryDelay_IsRejected()
    {
        var step = LlmStep("flaky") with
        {
            Retry = new WorkflowRetrySettings { InitialDelay = TimeSpan.FromSeconds(-1) }
        };

        Sut.Validate(Command([step])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_HumanGateTimeoutAboveTheCeiling_IsRejected()
    {
        // An unbounded gate parks a run and an operator approval forever, so the approval queue grows
        // with entries nobody can tell are still meaningful.
        _config.WorkflowSubmission.MaxHumanGateTimeout = TimeSpan.FromHours(1);

        var gate = new WorkflowStep
        {
            Name = "gate",
            Type = StepType.HumanGate,
            Configuration = new HumanGateStepConfiguration
            {
                EscalationMessage = "approve",
                ApprovalStrategy = ApprovalStrategy.AnyOf,
                Timeout = TimeSpan.FromHours(2)
            }
        };

        Sut.Validate(Command([gate])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_OversizedPromptInsideAStepConfiguration_IsRejected()
    {
        // The step's own name is within the cap; the cap has to reach into the configuration payload
        // too, which is where a caller can actually put megabytes of text.
        _config.WorkflowSubmission.MaxStringFieldLength = 16;

        var step = new WorkflowStep
        {
            Name = "short",
            Type = StepType.LlmCall,
            Configuration = new LlmCallStepConfiguration
            {
                SystemPrompt = new string('x', 17),
                ModelDeploymentKey = "k"
            }
        };

        Sut.Validate(Command([step])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_TooManySteps_IsRejected()
    {
        _config.WorkflowSubmission.MaxSteps = 2;
        var steps = Enumerable.Range(0, 3).Select(i => LlmStep($"s{i}")).ToList();

        Sut.Validate(Command(steps)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_FanOutAboveTheCap_IsRejected()
    {
        _config.WorkflowSubmission.MaxFanOutPerStep = 2;
        var targets = Enumerable.Range(0, 3).Select(i => LlmStep($"t{i}")).ToList();
        var edges = targets
            .Select(t => new WorkflowEdge { From = "source", To = t.Name, Type = EdgeType.ControlFlow })
            .ToList();

        var result = Sut.Validate(Command([LlmStep("source"), .. targets], edges));

        result.IsValid.Should().BeFalse();
    }
}
