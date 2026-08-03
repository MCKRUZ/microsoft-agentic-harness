using Domain.Common.Config.AI;

namespace Application.AI.Common.Evaluation.Models;

/// <summary>
/// Configures the fixed LLM-judge model used by <c>LlmJudgeMetric</c> and the
/// RAG metric pack.
/// </summary>
/// <remarks>
/// <para>
/// Bind via <c>services.Configure&lt;JudgeOptions&gt;(...)</c>. When unbound, the default
/// <see cref="Application.AI.Common.Evaluation.Interfaces.IJudgeChatClientProvider"/> falls back to the first available AI provider
/// from <c>IChatClientFactory.GetAvailableProviders()</c> and the supplied
/// <see cref="Deployment"/> — so a minimal setup only needs to set <see cref="Deployment"/>.
/// </para>
/// </remarks>
public sealed class JudgeOptions
{
    /// <summary>
    /// Which AI provider hosts the judge deployment. When <c>null</c>, the provider
    /// picks the first available type reported by <c>IChatClientFactory</c>.
    /// </summary>
    public AIAgentFrameworkClientType? ClientType { get; set; }

    /// <summary>
    /// Deployment / model identifier passed to <c>IChatClientFactory.GetChatClientAsync</c>.
    /// Required; the provider throws when empty.
    /// </summary>
    public string Deployment { get; set; } = string.Empty;
}
