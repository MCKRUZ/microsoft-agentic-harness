using System.Collections.Frozen;
using System.Text;
using Application.AI.Common.Interfaces.Agents;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;
using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Selects the best agent for a delegated task using a weighted scoring algorithm
/// across three dimensions: tool coverage, type alignment, and tier headroom.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm operates in three phases: filter (eliminate unsuitable candidates),
/// score (compute weighted composite), and select (pick the highest-scoring agent
/// with deterministic tie-breaking).
/// </para>
/// <para>
/// Weights are read from <c>AppConfig.AI.Orchestration.Subagent.CapabilityMatchWeights</c>
/// and normalized so they always sum to 1.0 regardless of configured values.
/// </para>
/// </remarks>
public sealed class CapabilityMatchStrategy : ISupervisorStrategy
{
    private const int MaxTierValue = 2;

    private static readonly FrozenSet<string> ExploreKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "search", "find", "read", "explore", "analyze" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> PlanKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "plan", "design", "architect", "structure" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> VerifyKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "test", "verify", "check", "validate" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ExecuteKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "execute", "run", "build", "create", "write", "modify" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly double _normalizedToolWeight;
    private readonly double _normalizedTypeWeight;
    private readonly double _normalizedHeadroomWeight;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapabilityMatchStrategy"/> class.
    /// </summary>
    /// <param name="configMonitor">
    /// Options monitor providing <see cref="AppConfig"/> with capability match weights.
    /// </param>
    public CapabilityMatchStrategy(IOptionsMonitor<AppConfig> configMonitor)
    {
        ArgumentNullException.ThrowIfNull(configMonitor);

        var weights = configMonitor.CurrentValue.AI.Orchestration.Subagent.CapabilityMatchWeights;
        var total = weights.ToolCoverage + weights.TypeAlignment + weights.TierHeadroom;

        if (total <= 0)
            total = 1.0;

        _normalizedToolWeight = weights.ToolCoverage / total;
        _normalizedTypeWeight = weights.TypeAlignment / total;
        _normalizedHeadroomWeight = weights.TierHeadroom / total;
    }

    /// <inheritdoc />
    public AgentSelection? SelectAgent(SupervisorDecisionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var candidates = FilterCandidates(context);
        if (candidates.Count == 0)
            return null;

        var classifiedType = ClassifyTask(context.TaskDescription);
        var scored = ScoreCandidates(candidates, context, classifiedType);

        return SelectTopCandidate(scored, candidates, classifiedType);
    }

    /// <summary>
    /// Removes agents that don't meet minimum autonomy level or lack any required tool overlap.
    /// </summary>
    private static List<AgentCandidate> FilterCandidates(SupervisorDecisionContext context)
    {
        var requiredTools = new HashSet<string>(context.RequiredCapabilities, StringComparer.OrdinalIgnoreCase);
        var results = new List<AgentCandidate>();

        foreach (var agent in context.AvailableAgents)
        {
            if (agent.AutonomyLevel < context.MinimumAutonomyLevel)
                continue;

            if (requiredTools.Count > 0)
            {
                var agentTools = new HashSet<string>(agent.AvailableTools, StringComparer.OrdinalIgnoreCase);
                if (!requiredTools.Any(agentTools.Contains))
                    continue;
            }

            results.Add(agent);
        }

        return results;
    }

    /// <summary>
    /// Computes a <see cref="CapabilityScore"/> for each candidate across the three weighted dimensions.
    /// </summary>
    private List<CapabilityScore> ScoreCandidates(
        List<AgentCandidate> candidates,
        SupervisorDecisionContext context,
        SubagentType classifiedType)
    {
        var requiredTools = new HashSet<string>(context.RequiredCapabilities, StringComparer.OrdinalIgnoreCase);
        var minimumTier = (int)context.MinimumAutonomyLevel;
        var scores = new List<CapabilityScore>(candidates.Count);

        foreach (var agent in candidates)
        {
            var toolCoverage = ComputeToolCoverage(requiredTools, agent.AvailableTools);
            var typeAlignment = ComputeTypeAlignment(agent.AgentType, classifiedType);
            var tierHeadroom = ComputeTierHeadroom((int)agent.AutonomyLevel, minimumTier);

            var totalScore = (_normalizedToolWeight * toolCoverage)
                           + (_normalizedTypeWeight * typeAlignment)
                           + (_normalizedHeadroomWeight * tierHeadroom);

            scores.Add(new CapabilityScore
            {
                AgentId = agent.AgentId,
                ToolCoverage = toolCoverage,
                TypeAlignment = typeAlignment,
                TierHeadroom = tierHeadroom,
                TotalScore = totalScore
            });
        }

        return scores;
    }

    /// <summary>
    /// Picks the highest-scored candidate with deterministic tie-breaking:
    /// prefer lower autonomy level (least privilege), then type match.
    /// </summary>
    private static AgentSelection SelectTopCandidate(
        List<CapabilityScore> scores,
        List<AgentCandidate> candidates,
        SubagentType classifiedType)
    {
        if (candidates.Count == 1)
        {
            return new AgentSelection
            {
                SelectedAgent = candidates[0],
                ConfidenceScore = 1.0,
                Reasoning = $"Single candidate: {candidates[0].AgentId} ({candidates[0].AgentType})"
            };
        }

        var paired = scores
            .Zip(candidates)
            .OrderByDescending(p => p.First.TotalScore)
            .ThenBy(p => (int)p.Second.AutonomyLevel)
            .ThenByDescending(p => p.Second.AgentType == classifiedType ? 1 : 0)
            .ToList();

        var (winnerScore, winner) = paired[0];

        return new AgentSelection
        {
            SelectedAgent = winner,
            ConfidenceScore = winnerScore.TotalScore,
            Reasoning = BuildReasoning(paired.Count, winner, winnerScore, classifiedType)
        };
    }

    private static string BuildReasoning(
        int candidateCount,
        AgentCandidate winner,
        CapabilityScore score,
        SubagentType classifiedType)
    {
        var sb = new StringBuilder();
        sb.Append($"Evaluated {candidateCount} candidates. ");
        sb.Append($"Selected {winner.AgentId} ({winner.AgentType}). ");
        sb.Append($"Task classified as {classifiedType}. ");
        sb.Append($"Score: {score.TotalScore:F3} ");
        sb.Append($"(ToolCoverage={score.ToolCoverage:F2}, ");
        sb.Append($"TypeAlignment={score.TypeAlignment:F2}, ");
        sb.Append($"TierHeadroom={score.TierHeadroom:F2}).");
        return sb.ToString();
    }

    private static double ComputeToolCoverage(
        HashSet<string> requiredTools,
        IReadOnlyList<string> agentTools)
    {
        if (requiredTools.Count == 0)
            return 1.0;

        var agentToolSet = new HashSet<string>(agentTools, StringComparer.OrdinalIgnoreCase);
        var overlap = requiredTools.Count(agentToolSet.Contains);
        return overlap / (double)requiredTools.Count;
    }

    private static double ComputeTypeAlignment(SubagentType agentType, SubagentType classifiedType)
    {
        if (agentType == classifiedType) return 1.0;
        if (agentType == SubagentType.General) return 0.5;
        return 0.0;
    }

    private static double ComputeTierHeadroom(int agentTier, int minimumTier)
    {
        return (agentTier - minimumTier + 1) / (double)(MaxTierValue + 1);
    }

    /// <summary>
    /// Classifies a task description into a <see cref="SubagentType"/> by counting
    /// keyword matches per category. Ties favor <see cref="SubagentType.Execute"/>
    /// (bias toward action). No matches yield <see cref="SubagentType.General"/>.
    /// </summary>
    private static SubagentType ClassifyTask(string taskDescription)
    {
        var tokens = Tokenize(taskDescription);

        var exploreCt = CountMatches(tokens, ExploreKeywords);
        var planCt = CountMatches(tokens, PlanKeywords);
        var verifyCt = CountMatches(tokens, VerifyKeywords);
        var executeCt = CountMatches(tokens, ExecuteKeywords);

        var maxCount = Math.Max(Math.Max(exploreCt, planCt), Math.Max(verifyCt, executeCt));
        if (maxCount == 0)
            return SubagentType.General;

        // Tie-break: prefer Execute (bias toward action)
        if (executeCt == maxCount) return SubagentType.Execute;
        if (exploreCt == maxCount) return SubagentType.Explore;
        if (planCt == maxCount) return SubagentType.Plan;
        return SubagentType.Verify;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var start = -1;

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                if (start < 0) start = i;
            }
            else
            {
                if (start >= 0)
                {
                    tokens.Add(text[start..i]);
                    start = -1;
                }
            }
        }

        if (start >= 0)
            tokens.Add(text[start..]);

        return tokens;
    }

    private static int CountMatches(List<string> tokens, FrozenSet<string> keywords)
    {
        var count = 0;
        foreach (var token in tokens)
        {
            if (keywords.Contains(token))
                count++;
        }

        return count;
    }
}
