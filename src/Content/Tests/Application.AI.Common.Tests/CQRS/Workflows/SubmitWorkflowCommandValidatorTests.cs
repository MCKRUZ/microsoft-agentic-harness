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
            // Approvers supplied deliberately: without them the required-content rule would also
            // reject this gate, and the test would keep passing after the timeout ceiling was removed.
            Configuration = new HumanGateStepConfiguration
            {
                EscalationMessage = "approve",
                ApprovalStrategy = ApprovalStrategy.AnyOf,
                Approvers = ["alice"],
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

    [Fact]
    public void Validate_HumanGateWithNoApprovers_IsRejected()
    {
        // Admitted, this parks the run and can never be answered by anyone — it holds a slot until it
        // times out, and no operator can act on it because none was named.
        var gate = new WorkflowStep
        {
            Name = "gate",
            Type = StepType.HumanGate,
            Configuration = new HumanGateStepConfiguration
            {
                EscalationMessage = "approve the spend",
                ApprovalStrategy = ApprovalStrategy.AnyOf,
                Approvers = []
            }
        };

        Sut.Validate(Command([gate])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_HumanGateWithABlankApprover_IsRejected()
    {
        var gate = new WorkflowStep
        {
            Name = "gate",
            Type = StepType.HumanGate,
            Configuration = new HumanGateStepConfiguration
            {
                EscalationMessage = "approve",
                ApprovalStrategy = ApprovalStrategy.AnyOf,
                Approvers = ["alice", "   "]
            }
        };

        Sut.Validate(Command([gate])).IsValid.Should().BeFalse();
    }

    /// <summary>A well-formed gate naming <paramref name="approvers"/>.</summary>
    private static WorkflowStep GateStep(params string[] approvers) => new()
    {
        Name = "gate",
        Type = StepType.HumanGate,
        Configuration = new HumanGateStepConfiguration
        {
            EscalationMessage = "approve the spend",
            ApprovalStrategy = ApprovalStrategy.AnyOf,
            Approvers = approvers
        }
    };

    private SubmitWorkflowCommand GateCommand(string? submitter, params string[] approvers) =>
        Command([GateStep(approvers)]) with { SubmitterApproverName = submitter };

    [Fact]
    public void Validate_HumanGate_WhenTheHostNamesNoApprovers_IsRejected()
    {
        // The shipped default, and deliberately fail-closed. A host that has not said who may approve
        // things has said that nothing may be approved — reading an unset roster as "anyone" would make
        // this a check that exists and enforces nothing, which is worse than not having it.
        _config.WorkflowSubmission.PermittedApprovers = [];

        var result = Sut.Validate(GateCommand("dave", "alice"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("does not recognise"));
    }

    [Fact]
    public void Validate_HumanGate_NamingSomeoneTheHostDoesNotKnow_IsRejected()
    {
        // A gate can only be answered by the people it names. One naming a stranger parks the workflow
        // for as long as the host permits and is then failed — so the honest place to say no is here,
        // where the author can still fix it.
        _config.WorkflowSubmission.PermittedApprovers = ["alice", "bob"];

        Sut.Validate(GateCommand("dave", "alice", "mallory")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_HumanGate_NamingPermittedApprovers_IsAccepted()
    {
        // The door this wave opens. Everything the gate needs now exists — a roster the host
        // recognises, a resume trigger, and a way to cancel — so a workflow containing one is no longer
        // a trap.
        _config.WorkflowSubmission.PermittedApprovers = ["alice", "bob"];

        Sut.Validate(GateCommand("dave", "alice", "bob")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_HumanGate_MatchesTheRosterCaseInsensitively()
    {
        // Rosters are operator-authored and tokens are issuer-minted; casing differences between them
        // are accidental, not semantic. Matching case-sensitively here while the decision path matches
        // case-insensitively would reject gates naming approvers who could in fact answer them.
        _config.WorkflowSubmission.PermittedApprovers = ["Alice@contoso.com"];

        Sut.Validate(GateCommand("dave", "alice@CONTOSO.com")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_HumanGate_NamingItsOwnSubmitter_IsRejected()
    {
        // A gate its author can answer is not an approval: the workflow pauses and continues on the
        // say-so of the person who wrote it, while the audit record shows a human decided.
        _config.WorkflowSubmission.PermittedApprovers = ["alice", "dave"];

        var result = Sut.Validate(GateCommand("dave", "alice", "dave"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("own submitter"));
    }

    [Fact]
    public void Validate_HumanGate_NamingItsSubmitterInDifferentCasing_IsStillRejected()
    {
        // Self-approval must not be reachable by typing your own name differently. The roster match is
        // case-insensitive, so a case-sensitive self-check would let the same person through.
        _config.WorkflowSubmission.PermittedApprovers = ["Dave@contoso.com"];

        Sut.Validate(GateCommand("dave@CONTOSO.com", "Dave@contoso.com")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_HumanGate_WhenTheSubmittersIdentityIsUnknown_IsRejected()
    {
        // The check cannot be performed, and the fail-open reading of that — "we could not tell, so
        // allow it" — is exactly the self-approval it exists to prevent. Refusing costs a caller with
        // no usable claim the ability to author gates; permitting costs everyone the guarantee.
        _config.WorkflowSubmission.PermittedApprovers = ["alice"];

        Sut.Validate(GateCommand(submitter: null, "alice")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_AWorkflowWithNoGate_IsUnaffectedByTheApproverRules()
    {
        // The roster governs gates, not submissions. A workflow that asks nobody to approve anything
        // must not be refused because the host has not named any approvers.
        _config.WorkflowSubmission.PermittedApprovers = [];

        Sut.Validate(Command([LlmStep("only")]) with { SubmitterApproverName = null })
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_HumanGateWithABlankMessage_IsRejected()
    {
        // The message is the only context most approvers will have.
        var gate = new WorkflowStep
        {
            Name = "gate",
            Type = StepType.HumanGate,
            Configuration = new HumanGateStepConfiguration
            {
                EscalationMessage = "   ",
                ApprovalStrategy = ApprovalStrategy.AnyOf,
                Approvers = ["alice"]
            }
        };

        Sut.Validate(Command([gate])).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(StepType.LlmCall)]
    [InlineData(StepType.ToolUse)]
    [InlineData(StepType.Retrieval)]
    [InlineData(StepType.ConditionalBranch)]
    public void Validate_StepMissingTheContentItsTypeRequires_IsRejected(StepType type)
    {
        WorkflowStepConfiguration configuration = type switch
        {
            StepType.LlmCall => new LlmCallStepConfiguration { SystemPrompt = "  ", ModelDeploymentKey = "k" },
            StepType.ToolUse => new ToolUseStepConfiguration { ToolName = "  " },
            StepType.Retrieval => new RetrievalWorkflowStepConfiguration { Query = "  " },
            _ => new ConditionalBranchStepConfiguration { ConditionExpression = "  " }
        };

        var step = new WorkflowStep { Name = "empty", Type = type, Configuration = configuration };

        Sut.Validate(Command([step])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_TokenRequestAboveTheCeiling_IsRejected()
    {
        // Tokens are the direct unit of inference spend, and no graph-size cap bounds them: a
        // one-step workflow can otherwise ask for an unbounded completion.
        _config.WorkflowSubmission.MaxTokensPerStep = 1000;
        var step = new WorkflowStep
        {
            Name = "expensive",
            Type = StepType.LlmCall,
            Configuration = new LlmCallStepConfiguration
            {
                SystemPrompt = "p", ModelDeploymentKey = "k", MaxTokens = 1001
            }
        };

        Sut.Validate(Command([step])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_TopKAboveTheCeiling_IsRejected()
    {
        _config.WorkflowSubmission.MaxTopK = 10;
        var step = new WorkflowStep
        {
            Name = "broad",
            Type = StepType.Retrieval,
            Configuration = new RetrievalWorkflowStepConfiguration { Query = "q", TopK = 11 }
        };

        Sut.Validate(Command([step])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ModelDeploymentOutsideTheHostsAllowList_IsRejected()
    {
        _config.AgentFramework.AvailableDeployments = ["gpt-4o", "gpt-4o-mini"];
        var step = new WorkflowStep
        {
            Name = "wrong-model",
            Type = StepType.LlmCall,
            Configuration = new LlmCallStepConfiguration
            {
                SystemPrompt = "p", ModelDeploymentKey = "some-other-model"
            }
        };

        Sut.Validate(Command([step])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ModelDeploymentInsideTheHostsAllowList_IsAccepted()
    {
        _config.AgentFramework.AvailableDeployments = ["gpt-4o"];
        var step = new WorkflowStep
        {
            Name = "right-model",
            Type = StepType.LlmCall,
            Configuration = new LlmCallStepConfiguration { SystemPrompt = "p", ModelDeploymentKey = "gpt-4o" }
        };

        Sut.Validate(Command([step])).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenTheHostDeclaresNoAllowList_AnyDeploymentKeyPassesThrough()
    {
        // Empty means the host has declared no allow-list, so the key resolves at run time. This is a
        // deliberate pass-through: an unrecognised key fails the step that used it and grants nothing.
        _config.AgentFramework.AvailableDeployments = [];

        Sut.Validate(Command([LlmStep("any")])).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_OversizedToolArgumentValue_IsRejected()
    {
        // The cap documents itself as covering "tool arguments", and the argument values are the part
        // of a tool step a caller can actually make enormous — the tool name is short by nature.
        _config.WorkflowSubmission.MaxStringFieldLength = 16;

        var step = new WorkflowStep
        {
            Name = "tool",
            Type = StepType.ToolUse,
            Configuration = new ToolUseStepConfiguration
            {
                ToolName = "file_system",
                InputParameters = new Dictionary<string, object?> { ["content"] = new string('x', 17) }
            }
        };

        Sut.Validate(Command([step])).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ToolArgumentWithinTheCap_IsAccepted()
    {
        _config.WorkflowSubmission.MaxStringFieldLength = 16;

        var step = new WorkflowStep
        {
            Name = "tool",
            Type = StepType.ToolUse,
            Configuration = new ToolUseStepConfiguration
            {
                ToolName = "fs",
                InputParameters = new Dictionary<string, object?> { ["content"] = "short", ["count"] = 3 }
            }
        };

        Sut.Validate(Command([step])).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("score.value > 5")]
    [InlineData("typeof(x)")]
    public void Validate_ConditionTheBranchEvaluatorWouldRefuse_IsRejected(string expression)
    {
        var branch = new WorkflowStep
        {
            Name = "branch",
            Type = StepType.ConditionalBranch,
            Configuration = new ConditionalBranchStepConfiguration { ConditionExpression = expression }
        };

        var edges = new List<WorkflowEdge>
        {
            new() { From = "branch", To = "yes", Type = EdgeType.ConditionalTrue },
            new() { From = "branch", To = "no", Type = EdgeType.ConditionalFalse }
        };

        Sut.Validate(Command([branch, LlmStep("yes"), LlmStep("no")], edges)).IsValid.Should().BeFalse();
    }
}
