using Application.AI.Common.CQRS.Workflows.Submit;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Domain.AI.Planner;
using Domain.AI.RAG.Enums;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Workflows;

/// <summary>
/// Tests for <see cref="WorkflowDefinitionMapper"/> — the translation from a caller's submitted
/// definition to the plan graph the harness actually runs.
/// </summary>
/// <remarks>
/// The properties pinned here are the ones where a silent mis-mapping does damage rather than throwing:
/// identifiers being minted rather than accepted, an omitted optional falling back to the domain
/// default instead of a hardcoded copy of it, branch targets coming from the labelled edges, and the
/// three domain capabilities that have no wire field staying at their safe defaults.
/// </remarks>
public sealed class WorkflowDefinitionMapperTests
{
    private static WorkflowStep LlmStep(string name, LlmCallStepConfiguration? configuration = null) => new()
    {
        Name = name,
        Type = StepType.LlmCall,
        Configuration = configuration ?? new LlmCallStepConfiguration
        {
            SystemPrompt = "do the thing",
            ModelDeploymentKey = "gpt-4"
        }
    };

    private static WorkflowDefinition Definition(
        IReadOnlyList<WorkflowStep> steps,
        IReadOnlyList<WorkflowEdge>? edges = null,
        WorkflowExecutionSettings? configuration = null) => new()
        {
            Name = "example-workflow",
            Steps = steps,
            Edges = edges ?? [],
            Configuration = configuration
        };

    [Fact]
    public void MapToPlanGraph_MintsFreshIdentifiersRatherThanReusingCallerNames()
    {
        var definition = Definition([LlmStep("first"), LlmStep("second")]);

        var first = WorkflowDefinitionMapper.MapToPlanGraph(definition);
        var second = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();

        // Two submissions of the identical definition must not collide: nothing the caller sent
        // becomes an identifier, so one caller cannot target another's records by resubmitting.
        first.Value!.Id.Should().NotBe(second.Value!.Id);
        first.Value.Steps.Select(s => s.Id)
            .Should().NotIntersectWith(second.Value.Steps.Select(s => s.Id));

        first.Value.Steps.Select(s => s.Name).Should().Equal("first", "second");
    }

    [Fact]
    public void MapToPlanGraph_OmittedOptionals_LeaveTheDomainDefaultsInPlace()
    {
        var definition = Definition([LlmStep("only")]);

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        result.IsSuccess.Should().BeTrue();
        var step = result.Value!.Steps.Single();

        // Compared against freshly-constructed domain records rather than against literals, so this
        // test cannot pass while quietly enshrining a second copy of a default that has since moved.
        var defaultLlm = new LlmCallConfig { SystemPrompt = "x", ModelDeploymentKey = "y" };
        var configuration = step.Configuration.Should().BeOfType<LlmCallConfig>().Subject;
        configuration.Temperature.Should().Be(defaultLlm.Temperature);
        configuration.MaxTokens.Should().Be(defaultLlm.MaxTokens);

        step.RetryPolicy.Should().Be(new RetryPolicy());
        step.Timeout.Should().Be(new PlanStep
        {
            Id = PlanStepId.New(), Name = "probe", Type = StepType.LlmCall,
            Configuration = defaultLlm, RetryPolicy = new RetryPolicy()
        }.Timeout);
        result.Value.Configuration.Should().Be(new PlanConfiguration());
    }

    [Fact]
    public void MapToPlanGraph_SuppliedOptionals_OverrideTheDefaults()
    {
        var definition = Definition(
            [
                LlmStep("only", new LlmCallStepConfiguration
                {
                    SystemPrompt = "p", ModelDeploymentKey = "k", Temperature = 0.1, MaxTokens = 99
                }) with
                {
                    Timeout = TimeSpan.FromSeconds(11),
                    RequiredAutonomyLevel = AutonomyLevel.Supervised,
                    Retry = new WorkflowRetrySettings
                    {
                        MaxRetries = 7,
                        InitialDelay = TimeSpan.FromSeconds(3),
                        Strategy = BackoffStrategy.Linear,
                        OnExhausted = ErrorRecovery.Escalate
                    }
                }
            ],
            configuration: new WorkflowExecutionSettings
            {
                PlanTimeout = TimeSpan.FromMinutes(3), MaxParallelSteps = 4
            });

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        result.IsSuccess.Should().BeTrue();
        var step = result.Value!.Steps.Single();
        var configuration = step.Configuration.Should().BeOfType<LlmCallConfig>().Subject;
        configuration.Temperature.Should().Be(0.1);
        configuration.MaxTokens.Should().Be(99);
        step.Timeout.Should().Be(TimeSpan.FromSeconds(11));
        step.RequiredAutonomyLevel.Should().Be(AutonomyLevel.Supervised);
        step.RetryPolicy.Should().Be(new RetryPolicy
        {
            MaxRetries = 7,
            InitialDelay = TimeSpan.FromSeconds(3),
            Strategy = BackoffStrategy.Linear,
            OnExhausted = ErrorRecovery.Escalate
        });
        result.Value.Configuration.PlanTimeout.Should().Be(TimeSpan.FromMinutes(3));
        result.Value.Configuration.MaxParallelSteps.Should().Be(4);
    }

    [Fact]
    public void MapToPlanGraph_PlanConfiguration_NeverTakesSubPlanDepthFromTheWire()
    {
        // MaxSubPlanDepth is a runtime recursion guard the host owns. There is no wire field for it,
        // and this pins that a caller-supplied execution block cannot move it by any route.
        var definition = Definition(
            [LlmStep("only")],
            configuration: new WorkflowExecutionSettings { PlanTimeout = TimeSpan.FromMinutes(2) });

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        result.Value!.Configuration.MaxSubPlanDepth.Should().Be(new PlanConfiguration().MaxSubPlanDepth);
    }

    [Fact]
    public void MapToPlanGraph_ConditionalBranch_TakesItsTargetsFromTheLabelledEdges()
    {
        var definition = Definition(
            [
                new WorkflowStep
                {
                    Name = "branch",
                    Type = StepType.ConditionalBranch,
                    Configuration = new ConditionalBranchStepConfiguration { ConditionExpression = "score > 5" }
                },
                LlmStep("approve"),
                LlmStep("reject")
            ],
            [
                new WorkflowEdge { From = "branch", To = "approve", Type = EdgeType.ConditionalTrue },
                new WorkflowEdge { From = "branch", To = "reject", Type = EdgeType.ConditionalFalse }
            ]);

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        result.IsSuccess.Should().BeTrue();
        var plan = result.Value!;
        var branch = plan.Steps.Single(s => s.Name == "branch");
        var configuration = branch.Configuration.Should().BeOfType<ConditionalBranchConfig>().Subject;

        configuration.ConditionExpression.Should().Be("score > 5");
        configuration.TrueEdgeTargetId.Should().Be(plan.Steps.Single(s => s.Name == "approve").Id);
        configuration.FalseEdgeTargetId.Should().Be(plan.Steps.Single(s => s.Name == "reject").Id);
    }

    [Theory]
    [InlineData(EdgeType.ConditionalTrue, "missing false arm")]
    [InlineData(EdgeType.ConditionalFalse, "missing true arm")]
    public void MapToPlanGraph_ConditionalBranch_WithOnlyOneArm_Fails(EdgeType present, string because)
    {
        var definition = Definition(
            [
                new WorkflowStep
                {
                    Name = "branch",
                    Type = StepType.ConditionalBranch,
                    Configuration = new ConditionalBranchStepConfiguration { ConditionExpression = "x" }
                },
                LlmStep("next")
            ],
            [new WorkflowEdge { From = "branch", To = "next", Type = present }]);

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        result.IsSuccess.Should().BeFalse(because);
        result.Errors.Should().ContainMatch("*branch*ConditionalTrue*ConditionalFalse*");
    }

    [Fact]
    public void MapToPlanGraph_ConditionalBranch_WithTwoTrueArms_FailsRatherThanPickingOne()
    {
        // The whole reason the wire omits ConditionalBranchConfig's own target fields is that two
        // statements of one fact can disagree. Two identically-labelled edges are the same defect in
        // a different place, so it must not resolve to "whichever came first".
        var definition = Definition(
            [
                new WorkflowStep
                {
                    Name = "branch",
                    Type = StepType.ConditionalBranch,
                    Configuration = new ConditionalBranchStepConfiguration { ConditionExpression = "x" }
                },
                LlmStep("a"), LlmStep("b"), LlmStep("c")
            ],
            [
                new WorkflowEdge { From = "branch", To = "a", Type = EdgeType.ConditionalTrue },
                new WorkflowEdge { From = "branch", To = "b", Type = EdgeType.ConditionalTrue },
                new WorkflowEdge { From = "branch", To = "c", Type = EdgeType.ConditionalFalse }
            ]);

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void MapToPlanGraph_Edges_ResolveNamesToTheMintedStepIdentifiers()
    {
        var definition = Definition(
            [LlmStep("first"), LlmStep("second")],
            [new WorkflowEdge { From = "first", To = "second", Type = EdgeType.DataFlow, Condition = "ok" }]);

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        var plan = result.Value!;
        var edge = plan.Edges.Should().ContainSingle().Subject;
        edge.From.Should().Be(plan.Steps.Single(s => s.Name == "first").Id);
        edge.To.Should().Be(plan.Steps.Single(s => s.Name == "second").Id);
        edge.Type.Should().Be(EdgeType.DataFlow);
        edge.Condition.Should().Be("ok");
    }

    [Fact]
    public void MapToPlanGraph_ToolUse_LeavesTheSandboxIsolationOverrideUnset()
    {
        // There is no wire field for it, and a caller able to set it could weaken the sandbox the
        // step it authored runs inside.
        var definition = Definition(
            [
                new WorkflowStep
                {
                    Name = "tool",
                    Type = StepType.ToolUse,
                    Configuration = new ToolUseStepConfiguration
                    {
                        ToolName = "file_system",
                        InputParameters = new Dictionary<string, object?> { ["path"] = "/tmp/x" }
                    }
                }
            ]);

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        var configuration = result.Value!.Steps.Single().Configuration
            .Should().BeOfType<ToolUseConfig>().Subject;
        configuration.ToolName.Should().Be("file_system");
        configuration.InputParameters.Should().ContainKey("path");
        configuration.IsolationLevelOverride.Should().BeNull();
    }

    [Fact]
    public void MapToPlanGraph_Retrieval_LeavesTheCollectionNameUnset()
    {
        // Naming a corpus is a cross-tenant read primitive, so the wire has no field for it and the
        // mapped step must fall back to whatever corpus the caller's own scope resolves.
        var definition = Definition(
            [
                new WorkflowStep
                {
                    Name = "search",
                    Type = StepType.Retrieval,
                    Configuration = new RetrievalWorkflowStepConfiguration
                    {
                        Query = "quarterly revenue",
                        Strategy = RetrievalStrategy.HybridVectorBm25,
                        TopK = 5,
                        UseMultiSource = true
                    }
                }
            ]);

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        var configuration = result.Value!.Steps.Single().Configuration
            .Should().BeOfType<RetrievalStepConfiguration>().Subject;
        configuration.Query.Should().Be("quarterly revenue");
        configuration.Strategy.Should().Be(RetrievalStrategy.HybridVectorBm25);
        configuration.TopK.Should().Be(5);
        configuration.UseMultiSource.Should().BeTrue();
        configuration.CollectionName.Should().BeNull();
    }

    [Fact]
    public void MapToPlanGraph_SubPlan_ReferencesTheChildByIdAndNeverInlinesIt()
    {
        var childId = Guid.NewGuid();
        var definition = Definition(
            [
                new WorkflowStep
                {
                    Name = "child",
                    Type = StepType.SubPlanInvocation,
                    Configuration = new SubPlanStepConfiguration
                    {
                        ChildWorkflowId = childId,
                        IsolateContext = false
                    }
                }
            ]);

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        var configuration = result.Value!.Steps.Single().Configuration
            .Should().BeOfType<SubPlanConfig>().Subject;
        configuration.ChildPlanId.Should().Be(new PlanId(childId));
        configuration.IsolateContext.Should().BeFalse();
        configuration.InlinePlanDefinition.Should().BeNull();
    }

    [Fact]
    public void MapToPlanGraph_HumanGate_CarriesEveryApprovalFieldThrough()
    {
        var definition = Definition(
            [
                new WorkflowStep
                {
                    Name = "gate",
                    Type = StepType.HumanGate,
                    Configuration = new HumanGateStepConfiguration
                    {
                        EscalationMessage = "approve the spend",
                        ApprovalStrategy = ApprovalStrategy.Quorum,
                        Approvers = ["alice", "bob"],
                        RiskLevel = RiskLevel.High,
                        Timeout = TimeSpan.FromHours(2)
                    }
                }
            ]);

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        var configuration = result.Value!.Steps.Single().Configuration
            .Should().BeOfType<HumanGateConfig>().Subject;
        configuration.EscalationMessage.Should().Be("approve the spend");
        configuration.ApprovalStrategy.Should().Be(ApprovalStrategy.Quorum);
        configuration.Approvers.Should().Equal("alice", "bob");
        configuration.RiskLevel.Should().Be(RiskLevel.High);
        configuration.Timeout.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void MapToPlanGraph_HumanGate_OmittedRiskAndTimeout_KeepTheDomainDefaults()
    {
        var reference = new HumanGateConfig
        {
            EscalationMessage = "x", ApprovalStrategy = ApprovalStrategy.AnyOf
        };

        var definition = Definition(
            [
                new WorkflowStep
                {
                    Name = "gate",
                    Type = StepType.HumanGate,
                    Configuration = new HumanGateStepConfiguration
                    {
                        EscalationMessage = "approve",
                        ApprovalStrategy = ApprovalStrategy.AnyOf
                    }
                }
            ]);

        var result = WorkflowDefinitionMapper.MapToPlanGraph(definition);

        var configuration = result.Value!.Steps.Single().Configuration
            .Should().BeOfType<HumanGateConfig>().Subject;
        configuration.RiskLevel.Should().Be(reference.RiskLevel);
        configuration.Timeout.Should().Be(reference.Timeout);
    }
}
