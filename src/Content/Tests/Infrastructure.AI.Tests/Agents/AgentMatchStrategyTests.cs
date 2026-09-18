using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Infrastructure.AI.Agents;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

public class AgentMatchStrategyTests
{
    private static AgentCandidate Candidate(
        string id, string description, string? category = null, string? domain = null, IReadOnlyList<string>? tags = null) => new()
    {
        AgentId = id,
        AgentType = SubagentType.NamedAgent,
        AutonomyLevel = AutonomyLevel.Restricted,
        AvailableTools = [],
        Description = description,
        Category = category,
        Domain = domain,
        Tags = tags ?? []
    };

    private static SupervisorDecisionContext Context(string task, params AgentCandidate[] candidates) => new()
    {
        TaskDescription = task,
        RequiredCapabilities = [],
        MinimumAutonomyLevel = AutonomyLevel.Restricted,
        AvailableAgents = candidates,
        CurrentDelegationDepth = 0,
        MaxDelegationDepth = 0
    };

    [Fact]
    public void SelectAgent_OneCandidateClearlyMatches_SelectsIt()
    {
        var strategy = new AgentMatchStrategy();
        var research = Candidate("research-agent", "Investigates prior art and synthesizes findings", category: "research");
        var billing = Candidate("billing-agent", "Handles invoices and payment reconciliation", category: "finance");

        var context = Context("find prior art for this approach", research, billing);

        var selection = strategy.SelectAgent(context);

        Assert.NotNull(selection);
        Assert.Equal("research-agent", selection!.SelectedAgent.AgentId);
        Assert.True(selection.ConfidenceScore > 0);
    }

    [Fact]
    public void SelectAgent_NoKeywordOverlapWithAnyCandidate_ReturnsNull()
    {
        var strategy = new AgentMatchStrategy();
        var research = Candidate("research-agent", "Investigates prior art and synthesizes findings");

        var context = Context("zzz qqq xyz123", research);

        var selection = strategy.SelectAgent(context);

        Assert.Null(selection);
    }

    [Fact]
    public void SelectAgent_NoCandidates_ReturnsNull()
    {
        var strategy = new AgentMatchStrategy();

        var selection = strategy.SelectAgent(Context("anything"));

        Assert.Null(selection);
    }

    [Fact]
    public void SelectAgent_CandidateWithNoDescriptionOrTags_IsSkippedNotCrashed()
    {
        var strategy = new AgentMatchStrategy();
        var blank = Candidate("blank-agent", string.Empty);
        var research = Candidate("research-agent", "Investigates prior art and synthesizes findings");

        var selection = strategy.SelectAgent(Context("find prior art", blank, research));

        Assert.NotNull(selection);
        Assert.Equal("research-agent", selection!.SelectedAgent.AgentId);
    }

    [Fact]
    public void SelectAgent_MatchesOnTagsAndCategory_NotJustDescription()
    {
        var strategy = new AgentMatchStrategy();
        var coder = Candidate("coder-agent", "General purpose assistant", category: "engineering", tags: ["python", "refactoring"]);

        var selection = strategy.SelectAgent(Context("refactor this python module", coder));

        Assert.NotNull(selection);
        Assert.Equal("coder-agent", selection!.SelectedAgent.AgentId);
    }

    [Fact]
    public void SelectAgent_HigherOverlapWins()
    {
        var strategy = new AgentMatchStrategy();
        var weak = Candidate("weak-agent", "General purpose assistant for various tasks");
        var strong = Candidate("strong-agent", "Deploys builds and runs deployment pipelines", tags: ["deploy", "pipeline"]);

        var selection = strategy.SelectAgent(Context("deploy the latest build pipeline", weak, strong));

        Assert.NotNull(selection);
        Assert.Equal("strong-agent", selection!.SelectedAgent.AgentId);
    }

    [Fact]
    public void SelectAgent_NullContext_Throws()
    {
        var strategy = new AgentMatchStrategy();
        Assert.Throws<ArgumentNullException>(() => strategy.SelectAgent(null!));
    }
}
