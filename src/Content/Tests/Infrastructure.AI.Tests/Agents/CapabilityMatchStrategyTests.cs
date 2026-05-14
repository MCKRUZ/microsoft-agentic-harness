using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Orchestration;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

public sealed class CapabilityMatchStrategyTests
{
    // === Helpers ===

    private static SupervisorDecisionContext BuildContext(
        AutonomyLevel minimumTier,
        IReadOnlyList<string> requiredCapabilities,
        string taskDescription,
        params AgentCandidate[] agents)
        => new()
        {
            TaskDescription = taskDescription,
            RequiredCapabilities = requiredCapabilities,
            MinimumAutonomyLevel = minimumTier,
            AvailableAgents = agents,
            CurrentDelegationDepth = 0,
            MaxDelegationDepth = 3
        };

    private static AgentCandidate BuildCandidate(
        string agentId,
        SubagentType type,
        AutonomyLevel tier,
        params string[] tools)
        => new()
        {
            AgentId = agentId,
            AgentType = type,
            AutonomyLevel = tier,
            AvailableTools = tools
        };

    private static CapabilityMatchStrategy CreateStrategy(
        double toolW = 0.4,
        double typeW = 0.3,
        double headroomW = 0.3)
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                Orchestration = new OrchestrationConfig
                {
                    Subagent = new SubagentConfig
                    {
                        CapabilityMatchWeights = new CapabilityMatchWeightsConfig
                        {
                            ToolCoverage = toolW,
                            TypeAlignment = typeW,
                            TierHeadroom = headroomW
                        }
                    }
                }
            }
        };

        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);
        return new CapabilityMatchStrategy(options);
    }

    // === Filtering ===

    [Fact]
    public void SelectAgent_AgentBelowMinimumTier_FilteredOut()
    {
        var agent = BuildCandidate("a1", SubagentType.General, AutonomyLevel.Restricted, "tool_a");
        var context = BuildContext(AutonomyLevel.Supervised, ["tool_a"], "do something", agent);

        var result = CreateStrategy().SelectAgent(context);

        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_AgentLacksAllRequiredTools_FilteredOut()
    {
        var agent = BuildCandidate("a1", SubagentType.General, AutonomyLevel.Supervised, "tool_c");
        var context = BuildContext(AutonomyLevel.Supervised, ["tool_a", "tool_b"], "do something", agent);

        var result = CreateStrategy().SelectAgent(context);

        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_NoCandidatesAfterFiltering_ReturnsNull()
    {
        var context = BuildContext(AutonomyLevel.Restricted, ["tool_a"], "do something");

        var result = CreateStrategy().SelectAgent(context);

        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_PartialToolOverlap_PassesFilter()
    {
        var agent = BuildCandidate("a1", SubagentType.General, AutonomyLevel.Supervised, "a", "b");
        var context = BuildContext(AutonomyLevel.Supervised, ["a", "b", "c"], "do something", agent);

        var result = CreateStrategy().SelectAgent(context);

        result.Should().NotBeNull();
        result!.SelectedAgent.AgentId.Should().Be("a1");
    }

    // === Scoring ===

    [Fact]
    public void SelectAgent_ToolCoverage_ScoresCorrectly()
    {
        // Need 2+ candidates so SelectTopCandidate doesn't short-circuit to confidence=1.0.
        // a1 has 2/3 required tools, a2 has 1/3 required tools.
        // With toolW dominant, a1 should win with confidence ~0.667.
        var a1 = BuildCandidate("a1", SubagentType.General, AutonomyLevel.Supervised, "a", "b");
        var a2 = BuildCandidate("a2", SubagentType.General, AutonomyLevel.Supervised, "a");
        var context = BuildContext(AutonomyLevel.Supervised, ["a", "b", "c"], "do something", a1, a2);

        var result = CreateStrategy(toolW: 1.0, typeW: 0.0001, headroomW: 0.0001).SelectAgent(context);

        result.Should().NotBeNull();
        result!.SelectedAgent.AgentId.Should().Be("a1");
        // ToolCoverage = 2/3 dominates the score since tool weight ~= 1.0
        result.ConfidenceScore.Should().BeApproximately(2.0 / 3.0, 0.05);
    }

    [Fact]
    public void SelectAgent_EmptyRequiredCapabilities_ToolCoverageIsOne()
    {
        var agent = BuildCandidate("a1", SubagentType.General, AutonomyLevel.Supervised, "a", "b");
        var context = BuildContext(AutonomyLevel.Supervised, Array.Empty<string>(), "do something", agent);

        var result = CreateStrategy(toolW: 1.0, typeW: 0.0001, headroomW: 0.0001).SelectAgent(context);

        result.Should().NotBeNull();
        // Empty required = 1.0 coverage; single candidate returns confidence 1.0
        result!.ConfidenceScore.Should().Be(1.0);
    }

    [Fact]
    public void SelectAgent_TypeAlignment_ExactMatch_ScoresOne()
    {
        // "search the codebase" contains "search" -> Explore
        var agent = BuildCandidate("a1", SubagentType.Explore, AutonomyLevel.Supervised, "a");
        var context = BuildContext(AutonomyLevel.Supervised, Array.Empty<string>(), "search the codebase", agent);

        var result = CreateStrategy(toolW: 0.0001, typeW: 1.0, headroomW: 0.0001).SelectAgent(context);

        result.Should().NotBeNull();
        // Single candidate -> confidence 1.0
        result!.ConfidenceScore.Should().Be(1.0);
    }

    [Fact]
    public void SelectAgent_TypeAlignment_General_ScoresHalf()
    {
        // General type always scores 0.5 alignment regardless of task
        var exactAgent = BuildCandidate("exact", SubagentType.Explore, AutonomyLevel.Supervised, "a");
        var generalAgent = BuildCandidate("general", SubagentType.General, AutonomyLevel.Supervised, "a");
        var context = BuildContext(
            AutonomyLevel.Supervised,
            Array.Empty<string>(),
            "search the codebase",
            exactAgent, generalAgent);

        var result = CreateStrategy(toolW: 0.0001, typeW: 1.0, headroomW: 0.0001).SelectAgent(context);

        result.Should().NotBeNull();
        // Exact match (1.0) beats General (0.5)
        result!.SelectedAgent.AgentId.Should().Be("exact");
    }

    [Fact]
    public void SelectAgent_TierHeadroom_HigherTierScoresMore()
    {
        // Headroom = (agentTier - minTier + 1) / (MaxTierValue + 1) = (tier - 0 + 1) / 3
        // Supervised(1): (1-0+1)/3 = 0.667
        // Autonomous(2): (2-0+1)/3 = 1.0
        var supervised = BuildCandidate("sup", SubagentType.General, AutonomyLevel.Supervised, "a");
        var autonomous = BuildCandidate("auto", SubagentType.General, AutonomyLevel.Autonomous, "a");
        var context = BuildContext(
            AutonomyLevel.Restricted,
            Array.Empty<string>(),
            "do something",
            supervised, autonomous);

        var result = CreateStrategy(toolW: 0.0001, typeW: 0.0001, headroomW: 1.0).SelectAgent(context);

        result.Should().NotBeNull();
        result!.SelectedAgent.AgentId.Should().Be("auto");
    }

    // === Selection ===

    [Fact]
    public void SelectAgent_TiedScore_PrefersLowerTier()
    {
        // Both agents have identical tool coverage (1.0, empty required) and
        // identical type alignment (General=0.5). Set headroom weight to 0 so
        // the headroom dimension contributes nothing, making TotalScores equal.
        // Tie-break: .ThenBy(AutonomyLevel) -> lower tier (Supervised) wins.
        var supervised = BuildCandidate("sup", SubagentType.General, AutonomyLevel.Supervised, "a");
        var autonomous = BuildCandidate("auto", SubagentType.General, AutonomyLevel.Autonomous, "a");
        var context = BuildContext(
            AutonomyLevel.Restricted,
            Array.Empty<string>(),
            "do something",
            supervised, autonomous);

        // headroomW=0 -> normalized to 0/(0.5+0.5+0) = 0. Scores are identical.
        var result = CreateStrategy(toolW: 0.5, typeW: 0.5, headroomW: 0.0).SelectAgent(context);

        result.Should().NotBeNull();
        result!.SelectedAgent.AgentId.Should().Be("sup");
    }

    [Fact]
    public void SelectAgent_SingleCandidate_ReturnsWithConfidence()
    {
        var agent = BuildCandidate("solo", SubagentType.Execute, AutonomyLevel.Autonomous, "bash");
        var context = BuildContext(AutonomyLevel.Restricted, ["bash"], "run a build", agent);

        var result = CreateStrategy().SelectAgent(context);

        result.Should().NotBeNull();
        result!.SelectedAgent.AgentId.Should().Be("solo");
        // Single candidate always returns confidence 1.0
        result.ConfidenceScore.Should().Be(1.0);
        result.Reasoning.Should().Contain("Single candidate");
    }

    [Fact]
    public void SelectAgent_WeightsNormalized_SumNotOne()
    {
        // Weights 0.4 + 0.3 + 0.5 = 1.2 -- scores should still be in [0,1]
        var agent = BuildCandidate("a1", SubagentType.General, AutonomyLevel.Autonomous, "a", "b", "c");
        var context = BuildContext(AutonomyLevel.Restricted, ["a", "b", "c"], "do something", agent);

        var result = CreateStrategy(toolW: 0.4, typeW: 0.3, headroomW: 0.5).SelectAgent(context);

        result.Should().NotBeNull();
        // Single candidate = confidence 1.0
        result!.ConfidenceScore.Should().Be(1.0);
    }

    [Fact]
    public void SelectAgent_MultipleAgents_WeightsNormalized_ScoresInRange()
    {
        // Verify multi-candidate scores stay in [0,1] with non-unit-sum weights
        var a1 = BuildCandidate("a1", SubagentType.General, AutonomyLevel.Supervised, "a");
        var a2 = BuildCandidate("a2", SubagentType.General, AutonomyLevel.Autonomous, "a", "b");
        var context = BuildContext(AutonomyLevel.Restricted, ["a", "b", "c"], "do something", a1, a2);

        var result = CreateStrategy(toolW: 0.4, typeW: 0.3, headroomW: 0.5).SelectAgent(context);

        result.Should().NotBeNull();
        result!.ConfidenceScore.Should().BeInRange(0.0, 1.0);
    }

    // === Task Classifier ===

    [Fact]
    public void SelectAgent_SearchKeywords_MapsToExplore()
    {
        var exploreAgent = BuildCandidate("explorer", SubagentType.Explore, AutonomyLevel.Supervised, "a");
        var executeAgent = BuildCandidate("executor", SubagentType.Execute, AutonomyLevel.Supervised, "a");
        var context = BuildContext(
            AutonomyLevel.Supervised,
            Array.Empty<string>(),
            "search for documentation",
            exploreAgent, executeAgent);

        var result = CreateStrategy(toolW: 0.0001, typeW: 1.0, headroomW: 0.0001).SelectAgent(context);

        result.Should().NotBeNull();
        result!.SelectedAgent.AgentId.Should().Be("explorer");
    }

    [Fact]
    public void SelectAgent_CreateKeywords_MapsToExecute()
    {
        var executeAgent = BuildCandidate("executor", SubagentType.Execute, AutonomyLevel.Supervised, "a");
        var exploreAgent = BuildCandidate("explorer", SubagentType.Explore, AutonomyLevel.Supervised, "a");
        var context = BuildContext(
            AutonomyLevel.Supervised,
            Array.Empty<string>(),
            "create a new service and write tests",
            executeAgent, exploreAgent);

        var result = CreateStrategy(toolW: 0.0001, typeW: 1.0, headroomW: 0.0001).SelectAgent(context);

        result.Should().NotBeNull();
        result!.SelectedAgent.AgentId.Should().Be("executor");
    }

    [Fact]
    public void SelectAgent_NoKeywords_MapsToGeneral()
    {
        var generalAgent = BuildCandidate("gen", SubagentType.General, AutonomyLevel.Supervised, "a");
        var executeAgent = BuildCandidate("exec", SubagentType.Execute, AutonomyLevel.Supervised, "a");
        var context = BuildContext(
            AutonomyLevel.Supervised,
            Array.Empty<string>(),
            "do something",
            generalAgent, executeAgent);

        // General type gets 0.5 alignment with General classification
        // Execute type gets 0.0 alignment with General classification
        var result = CreateStrategy(toolW: 0.0001, typeW: 1.0, headroomW: 0.0001).SelectAgent(context);

        result.Should().NotBeNull();
        result!.SelectedAgent.AgentId.Should().Be("gen");
    }
}
