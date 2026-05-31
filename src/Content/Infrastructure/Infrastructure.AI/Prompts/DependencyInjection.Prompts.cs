using Application.AI.Common.Prompts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Prompts;

/// <summary>
/// DI registrations for the prompt registry (Sub-phase 5.3).
/// </summary>
public static class PromptRegistryDependencyInjection
{
    /// <summary>
    /// Registers <see cref="IPromptRegistry"/> (file-backed at <paramref name="promptsRootPath"/>),
    /// <see cref="IPromptRenderer"/> (Scriban, variable-only), and <see cref="IPromptUsageRecorder"/>
    /// (OTel-stamping). Idempotent — safe to call from any composition root.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="promptsRootPath">
    /// Absolute or process-cwd-relative path to the <c>prompts/</c> folder. When the
    /// folder does not exist at registration time, the registry returns empty results
    /// (no exception) — useful for hosts that ship with no prompts.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddPromptRegistry(
        this IServiceCollection services,
        string promptsRootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptsRootPath);

        services.AddSingleton<IPromptRegistry>(sp =>
            new FilePromptRegistry(
                promptsRootPath,
                sp.GetRequiredService<ILogger<FilePromptRegistry>>()));

        services.AddSingleton<IPromptRenderer, ScribanPromptRenderer>();
        services.AddSingleton<IPromptUsageRecorder, OtelPromptUsageRecorder>();

        // Request-scoped accumulator for MediatR's PromptUsageTrackingBehavior. Scoped so
        // every request gets a fresh bag; cross-request state never leaks. Services that
        // record directly (e.g. ConversationFactExtractor) do NOT touch the bag — they call
        // IPromptUsageRecorder themselves and stay out of the auto-record pipeline.
        services.AddScoped<IPromptUsageBag, InMemoryPromptUsageBag>();

        return services;
    }
}
