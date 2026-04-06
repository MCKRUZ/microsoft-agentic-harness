using Application.Common.Interfaces.Agent;
using Application.Common.Logging;
using Application.Common.OpenTelemetry.Conventions;

namespace Application.Common.Extensions;

/// <summary>
/// Convenience extensions for <see cref="IAgentExecutionContext"/> to reduce
/// repetitive null-checking patterns in behaviors, handlers, and services.
/// </summary>
public static class AgentContextExtensions
{
    private static readonly IReadOnlyDictionary<string, object> EmptyTags =
        new Dictionary<string, object>().AsReadOnly();

    /// <summary>
    /// Returns whether an agent context has been initialized (an agent is executing).
    /// </summary>
    public static bool IsActive(this IAgentExecutionContext context) =>
        context.AgentId is not null;

    /// <summary>
    /// Returns a short display identifier for logging:
    /// <c>"agentId@turn-3"</c> or <c>"no-agent"</c> when outside an agent context.
    /// For console formatter output with parent hierarchy, see
    /// <see cref="LoggingHelper.GetAgentDisplayName"/>.
    /// </summary>
    public static string GetDisplayIdentifier(this IAgentExecutionContext context) =>
        context.AgentId is not null
            ? $"{context.AgentId}@turn-{context.TurnNumber}"
            : "no-agent";

    /// <summary>
    /// Creates an <see cref="AgentLogScope"/> from the execution context, suitable for
    /// <c>ILogger.BeginScope</c>. Returns <c>null</c> when not in an agent context.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="AgentLogScope"/> (not raw key-value pairs) so that
    /// <see cref="AgentScopeProvider"/> and formatters can recognize the scope.
    /// </remarks>
    public static AgentLogScope? ToAgentLogScope(this IAgentExecutionContext context) =>
        context.AgentId is not null
            ? new AgentLogScope(
                AgentId: context.AgentId,
                ConversationId: context.ConversationId,
                TurnNumber: context.TurnNumber)
            : null;

    /// <summary>
    /// Returns OpenTelemetry-compatible tag dictionary for span enrichment.
    /// Uses <see cref="AgenticSemanticConventions"/> keys for consistency
    /// with the harness tracing pipeline. Returns a static empty instance
    /// when not in agent context.
    /// </summary>
    public static IReadOnlyDictionary<string, object> ToTelemetryTags(
        this IAgentExecutionContext context)
    {
        if (context.AgentId is null)
            return EmptyTags;

        var tags = new Dictionary<string, object>
        {
            [AgenticSemanticConventions.Agent.Name] = context.AgentId
        };

        if (context.ConversationId is not null)
            tags[AgenticSemanticConventions.Agent.ConversationId] = context.ConversationId;

        if (context.TurnNumber is not null)
            tags[AgenticSemanticConventions.Agent.TurnIndex] = context.TurnNumber.Value;

        return tags;
    }
}
