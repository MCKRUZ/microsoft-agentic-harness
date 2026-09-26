using Application.AI.Common.Interfaces.Telemetry;

namespace Application.AI.Common.Services.Telemetry;

/// <summary>
/// Default <see cref="IAgentTelemetryAttribution"/> for hosts that have not opted into an external
/// agent-governance integration. Publishes nothing and allocates nothing.
/// </summary>
/// <remarks>
/// Benign rather than throwing. An absent governance integration is the ordinary case, not a
/// misconfiguration, and a turn must not fail because nothing is listening — which is the opposite of
/// the <c>NotConfigured*</c> placeholders used where a feature is switched on but left unwired.
/// </remarks>
public sealed class NoOpAgentTelemetryAttribution : IAgentTelemetryAttribution
{
    /// <inheritdoc />
    public IDisposable BeginTurn(string agentId, string conversationId)
        => NoAgentTelemetryAttributionScope.Instance;
}
