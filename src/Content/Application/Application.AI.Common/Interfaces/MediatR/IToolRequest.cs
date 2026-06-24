namespace Application.AI.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for MediatR requests that represent a tool invocation. Consumed by the tool-aware
/// pipeline behaviors (<c>HookBehavior</c>, <c>ResponseSanitizationBehavior</c>,
/// <c>ToolOutputCompressionBehavior</c>) when a consumer routes tool calls through MediatR.
/// </summary>
/// <remarks>
/// The agent's own autonomous tool calls do <em>not</em> flow through MediatR — they are authorized on
/// the live tool path by <c>IToolInvocationGovernor</c> (via <c>GovernedAIFunction</c>). This marker
/// therefore has no production implementers today; it remains an extension point for consumers that
/// dispatch tool invocations as MediatR requests.
/// </remarks>
public interface IToolRequest
{
    /// <summary>Gets the tool name or key (e.g., "file_system", "web_fetch").</summary>
    string ToolName { get; }
}
