using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;

namespace Application.AI.Common.Prompts;

/// <summary>
/// Default <see cref="IPromptUsageStore"/> used when durable prompt-usage persistence is
/// disabled (the default). Appends are dropped; queries return empty result sets.
/// </summary>
/// <remarks>
/// <para>
/// Prompt-usage persistence is opt-in (<c>AppConfig:AI:PromptUsage:PersistenceEnabled</c>).
/// When it is off, the real <c>EfCorePromptUsageStore</c> is not registered — yet the
/// globally-scanned MediatR handlers that query it (prompt-version comparison, trace replay)
/// are always registered. This No-op keeps those handlers constructible in every host.
/// </para>
/// <para>
/// Empty results are the correct semantic here, not an error: with persistence off there are
/// genuinely no recorded rows to return. When persistence is enabled the EfCore store is
/// registered instead of this one (the two live in mutually exclusive registration branches).
/// </para>
/// </remarks>
public sealed class NullPromptUsageStore : IPromptUsageStore
{
    /// <inheritdoc />
    public Task AppendAsync(PromptUsageRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PromptUsageRecord>> QueryByTraceIdAsync(string traceId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PromptUsageRecord>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<PromptUsageRecord>> QueryByCaseIdAsync(string caseId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PromptUsageRecord>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<PromptUsageRecord>> QueryByPromptNameAsync(string promptName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PromptUsageRecord>>([]);
}
