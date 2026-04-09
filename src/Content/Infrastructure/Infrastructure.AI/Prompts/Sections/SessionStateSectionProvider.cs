using System.Text;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Prompts;

namespace Infrastructure.AI.Prompts.Sections;

/// <summary>
/// Provides the session state section — current turn number, budget utilization,
/// and active component breakdown. Not cacheable because state changes every turn.
/// </summary>
public sealed class SessionStateSectionProvider : IPromptSectionProvider
{
    private readonly IAgentExecutionContext _executionContext;
    private readonly IContextBudgetTracker _budgetTracker;

    /// <summary>
    /// Initializes a new instance of <see cref="SessionStateSectionProvider"/>.
    /// </summary>
    /// <param name="executionContext">The scoped agent execution context.</param>
    /// <param name="budgetTracker">Tracks token allocations per agent.</param>
    public SessionStateSectionProvider(
        IAgentExecutionContext executionContext,
        IContextBudgetTracker budgetTracker)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(budgetTracker);

        _executionContext = executionContext;
        _budgetTracker = budgetTracker;
    }

    /// <inheritdoc />
    public SystemPromptSectionType SectionType => SystemPromptSectionType.SessionState;

    /// <inheritdoc />
    public Task<SystemPromptSection?> GetSectionAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Session State");
        builder.AppendLine();

        if (_executionContext.TurnNumber.HasValue)
        {
            builder.AppendLine($"- Current turn: {_executionContext.TurnNumber.Value}");
        }

        var totalAllocated = _budgetTracker.GetTotalAllocated(agentId);
        if (totalAllocated > 0)
        {
            builder.AppendLine($"- Tokens allocated: {totalAllocated:N0}");

            var breakdown = _budgetTracker.GetBreakdown(agentId);
            if (breakdown.Count > 0)
            {
                builder.AppendLine("- Budget breakdown:");
                foreach (var (component, tokens) in breakdown)
                {
                    builder.AppendLine($"  - {component}: {tokens:N0} tokens");
                }
            }
        }

        var content = builder.ToString().TrimEnd();

        // If we only have the header with no data, skip the section
        if (content == "# Session State")
            return Task.FromResult<SystemPromptSection?>(null);

        var section = new SystemPromptSection(
            Name: "Session State",
            Type: SystemPromptSectionType.SessionState,
            Priority: 50,
            IsCacheable: false,
            EstimatedTokens: TokenEstimationHelper.EstimateTokens(content),
            Content: content);

        return Task.FromResult<SystemPromptSection?>(section);
    }
}
