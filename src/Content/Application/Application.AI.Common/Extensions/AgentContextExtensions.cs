using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Logging;

namespace Application.AI.Common.Extensions;

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
    /// <see cref="Application.Common.Logging.LoggingHelper.GetExecutorDisplayName"/>.
    /// </summary>
    public static string GetDisplayIdentifier(this IAgentExecutionContext context) =>
        context.AgentId is not null
            ? $"{context.AgentId}@turn-{context.TurnNumber}"
            : "no-agent";

    /// <summary>
    /// Creates an <see cref="ExecutionScope"/> from the agent execution context, suitable for
    /// <c>ILogger.BeginScope</c>. Returns <c>null</c> when not in an agent context.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="ExecutionScope"/> (not raw key-value pairs) so that
    /// <see cref="ExecutionScopeProvider"/> and formatters can recognize the scope.
    /// Maps agent concepts to generic execution fields.
    /// </remarks>
    public static ExecutionScope? ToExecutionScope(this IAgentExecutionContext context) =>
        context.AgentId is not null
            ? new ExecutionScope(
                ExecutorId: context.AgentId,
                CorrelationId: context.ConversationId,
                StepNumber: context.TurnNumber)
            : null;

    /// <summary>
    /// Returns OpenTelemetry-compatible tag dictionary for span enrichment.
    /// Uses <see cref="AgentConventions"/> keys for consistency
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
            [AgentConventions.Name] = context.AgentId
        };

        if (context.ConversationId is not null)
            tags[AgentConventions.ConversationId] = context.ConversationId;

        if (context.TurnNumber is not null)
            tags[AgentConventions.TurnIndex] = context.TurnNumber.Value;

        return tags;
    }
}
