using Domain.Common.Config.AI;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Evaluation.Interfaces;

/// <summary>
/// Resolves a fixed, configured <see cref="IChatClient"/> for LLM-judge evaluations.
/// Bypasses runtime tier-routing/escalation/quality-feedback components so the judge
/// model is the same across runs — preserving the reproducibility invariant the eval
/// framework exists to provide.
/// </summary>
/// <remarks>
/// <para>
/// Production agent turns route through <c>IModelRouter</c> for cost-aware tier
/// selection; eval judging deliberately does NOT, because:
/// <list type="bullet">
///   <item><description>Quality-feedback signals from production traffic would shift the judge tier between eval runs, contaminating score comparability.</description></item>
///   <item><description>Per-call complexity classification can itself invoke an LLM, adding nondeterministic cost the eval framework can't account for.</description></item>
///   <item><description>Eval consumers expect "same dataset + same agent + same judge config = same scores".</description></item>
/// </list>
/// </para>
/// <para>
/// Implementations should cache the resolved client by configuration key so a
/// large suite does not construct one HTTP/SDK client per case.
/// </para>
/// </remarks>
public interface IJudgeChatClientProvider
{
    /// <summary>
    /// Returns the configured judge chat client. Safe to call concurrently — implementations
    /// must be thread-safe and should cache the resolved client.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A ready-to-use <see cref="IChatClient"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when judge configuration is missing or no AI provider is available.
    /// </exception>
    Task<IChatClient> GetJudgeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns a judge chat client for an explicit provider + deployment, rather than the
    /// configured default. Used by a judge panel to resolve each panelist's model.
    /// Shares the same cache as <see cref="GetJudgeAsync(CancellationToken)"/>, so the
    /// default judge and a panelist requesting the same model reuse one client.
    /// </summary>
    /// <param name="clientType">The provider that hosts the panelist's model.</param>
    /// <param name="deployment">The deployment / model identifier for the panelist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A ready-to-use <see cref="IChatClient"/> for the requested model.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="deployment"/> is empty or the requested provider is unavailable.
    /// </exception>
    Task<IChatClient> GetJudgeAsync(
        AIAgentFrameworkClientType clientType,
        string deployment,
        CancellationToken cancellationToken);
}
