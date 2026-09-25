namespace Application.AI.Common.Interfaces.Telemetry;

/// <summary>
/// Publishes the identity of the agent running the current turn so that telemetry emitted during
/// that turn can be attributed to it by an external agent-governance platform.
/// </summary>
/// <remarks>
/// <para>
/// This exists because attribution has to be established <em>before</em> the turn's spans are
/// created, on the turn's own thread. A trace exporter runs later, on a background batching thread,
/// where ambient context is empty — so an exporter that needs to know which agent produced a span
/// can only learn it from something stamped onto the span at the moment it started.
/// </para>
/// <para>
/// The default implementation does nothing. Only a host that has opted into an external
/// agent-governance integration replaces it, so the ordinary case carries no cost and publishes
/// nothing.
/// </para>
/// <para>
/// The returned scope must be disposed at the end of the turn, and the caller is expected to hold it
/// for the whole turn — the values have to remain in effect while the turn's spans are being created,
/// not merely at the instant the turn begins.
/// </para>
/// </remarks>
public interface IAgentTelemetryAttribution
{
    /// <summary>
    /// Publishes attribution for the turn that is about to run.
    /// </summary>
    /// <param name="agentId">
    /// The harness's id for the agent running this turn, as carried on the agent-scoped request. Used
    /// both to select the agent's configured external identity and as its reported name.
    /// </param>
    /// <param name="conversationId">
    /// The durable conversation this turn belongs to. Governance platforms group runs into sessions by
    /// this value, so omitting it costs the grouping rather than the attribution.
    /// </param>
    /// <returns>
    /// A scope that keeps the attribution in effect until disposed. Implementations must return a
    /// non-null disposable even when they publish nothing, so callers never branch on the result.
    /// </returns>
    IDisposable BeginTurn(string agentId, string conversationId);
}
