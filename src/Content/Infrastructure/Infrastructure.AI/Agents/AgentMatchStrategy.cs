using Application.AI.Common.Interfaces.Agents;
using Domain.AI.Orchestration;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Selects the best agent for a cold, un-owned request by keyword overlap between the request
/// text and each candidate's <c>AGENT.md</c> description/tags/category/domain. Registered under
/// the keyed name <c>"agent-match"</c>, alongside <see cref="CapabilityMatchStrategy"/>'s
/// <c>"capability-match"</c> — a second implementation of the same <see cref="ISupervisorStrategy"/>
/// contract, not a change to the existing one.
/// </summary>
/// <remarks>
/// <see cref="CapabilityMatchStrategy"/> scores on tool coverage and a fixed archetype-type
/// alignment — both dimensions that only make sense once a task has declared what tools it
/// needs and is being weighed against the five built-in subagent archetypes. A front-door
/// request has neither: no tools have been requested yet, and every real <c>AGENT.md</c> agent
/// is <see cref="Domain.AI.Agents.SubagentType.NamedAgent"/>, a type the existing strategy's
/// alignment dimension can't score. This strategy matches on the fields that <em>are</em>
/// meaningful before any agent has run — <see cref="AgentCandidate.Description"/>,
/// <see cref="AgentCandidate.Tags"/>, <see cref="AgentCandidate.Category"/>, and
/// <see cref="AgentCandidate.Domain"/> — using the same tokenize-and-count technique
/// <see cref="CapabilityMatchStrategy"/> already uses internally, via <see cref="TextTokenizer"/>.
/// </remarks>
public sealed class AgentMatchStrategy : ISupervisorStrategy
{
    /// <inheritdoc/>
    public AgentSelection? SelectAgent(SupervisorDecisionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.AvailableAgents.Count == 0)
            return null;

        var taskTokens = new HashSet<string>(TextTokenizer.Tokenize(context.TaskDescription), StringComparer.OrdinalIgnoreCase);
        if (taskTokens.Count == 0)
            return null;

        AgentCandidate? bestCandidate = null;
        var bestOverlap = 0;
        var bestScore = 0.0;

        // Deterministic tie-break: first candidate in registry order wins on equal overlap.
        foreach (var candidate in context.AvailableAgents)
        {
            var candidateTokens = new HashSet<string>(TokenizeCandidate(candidate), StringComparer.OrdinalIgnoreCase);
            if (candidateTokens.Count == 0)
                continue;

            var overlap = taskTokens.Count(candidateTokens.Contains);
            if (overlap == 0 || overlap <= bestOverlap)
                continue;

            // Overlap coefficient: matches normalized by the smaller set, so a short description
            // isn't penalized against a long task message and vice versa.
            var score = overlap / (double)Math.Min(taskTokens.Count, candidateTokens.Count);

            bestCandidate = candidate;
            bestOverlap = overlap;
            bestScore = score;
        }

        if (bestCandidate is null)
            return null;

        return new AgentSelection
        {
            SelectedAgent = bestCandidate,
            ConfidenceScore = Math.Clamp(bestScore, 0.0, 1.0),
            Reasoning = $"Matched {bestOverlap} keyword(s) against {bestCandidate.AgentId}'s description/tags/category/domain."
        };
    }

    private static IEnumerable<string> TokenizeCandidate(AgentCandidate candidate)
    {
        var text = string.Join(' ', new[] { candidate.Description, candidate.Category, candidate.Domain }
            .Where(s => !string.IsNullOrEmpty(s))
            .Concat(candidate.Tags));

        return TextTokenizer.Tokenize(text);
    }
}
