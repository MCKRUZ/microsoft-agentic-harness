using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Builds a Foundry Responses <see cref="AIAgent"/> (direct inference) from an Azure AI Foundry
/// project endpoint, keeping the Azure SDK dependency out of the Application layer.
/// </summary>
/// <remarks>
/// <para>
/// The Foundry Responses surface (<c>AIProjectClient.AsAIAgent(...)</c>) returns a
/// <c>Microsoft.Agents.AI.ChatClientAgent</c> rather than an <see cref="IChatClient"/>, so it cannot
/// flow through <c>IChatClientFactory</c>. This abstraction lets <c>AgentFactory</c> (Application
/// layer) construct that agent while the concrete <c>AIProjectClient</c> wiring and the Azure SDK
/// packages live entirely in Infrastructure — preserving the Clean Architecture rule that the
/// Application layer compiles against only <c>Microsoft.Extensions.*</c> abstractions.
/// </para>
/// <para>
/// The harness middleware pipeline is injected via the <paramref name="clientFactory"/> hook so the
/// Foundry-hosted inference path retains OpenTelemetry, function invocation, observability,
/// prerequisite gating, and distributed caching exactly as the other providers do.
/// </para>
/// <para>
/// Implementations are registered only when <c>AppConfig.AI.AIFoundry.IsConfigured</c> is true.
/// When the provider is absent, <c>AgentFactory</c> surfaces a configuration error for the
/// <c>FoundryResponses</c> client type rather than failing silently.
/// </para>
/// </remarks>
public interface IFoundryAgentProvider
{
    /// <summary>
    /// Creates a Foundry Responses agent for direct inference. No server-managed agent resource is
    /// created — the model, instructions, and tools are supplied at runtime from
    /// <paramref name="options"/>.
    /// </summary>
    /// <param name="model">The Foundry model deployment name (e.g. <c>gpt-4o-mini</c>).</param>
    /// <param name="options">
    /// The agent definition the harness composed (name, description, instructions, tools,
    /// temperature, context providers).
    /// </param>
    /// <param name="clientFactory">
    /// Wraps the inner Foundry <see cref="IChatClient"/> with the harness middleware pipeline before
    /// the agent uses it. Invoked once during agent construction.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The configured <see cref="AIAgent"/> ready for use.</returns>
    Task<AIAgent> CreateAgentAsync(
        string model,
        ChatClientAgentOptions options,
        Func<IChatClient, IChatClient> clientFactory,
        CancellationToken cancellationToken = default);
}
