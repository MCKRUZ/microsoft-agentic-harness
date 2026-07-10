using Application.AI.Common.Prompts.Interfaces;
using Domain.Common.Config.AI;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Prompts.Persistence;
using Microsoft.EntityFrameworkCore;
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
    /// <see cref="IPromptRenderer"/> (Scriban, variable-only), <see cref="IPromptUsageRecorder"/>
    /// (OTel-stamping), and <see cref="IPromptUsageBag"/> (request-scoped accumulator for the
    /// auto-tracking MediatR behavior).
    /// </summary>
    /// <remarks>
    /// Persistence-only-overload — durable SQLite usage history is OFF. Call the
    /// <see cref="AddPromptRegistry(IServiceCollection, string, PromptUsageOptions)"/>
    /// overload to opt in.
    /// </remarks>
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
        => AddPromptRegistry(services, promptsRootPath, new PromptUsageOptions());

    /// <summary>
    /// Registers the prompt registry pipeline with optional SQLite persistence.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="promptsRootPath">Absolute or process-cwd-relative path to the <c>prompts/</c> folder.</param>
    /// <param name="usageOptions">
    /// Persistence configuration. When <see cref="PromptUsageOptions.PersistenceEnabled"/>
    /// is <c>true</c>, the public <see cref="IPromptUsageRecorder"/> becomes a
    /// <see cref="CompositePromptUsageRecorder"/> fanning out to OTel + SQLite, the
    /// <see cref="PromptUsageDbContext"/> is registered with the supplied connection
    /// string, and the schema is ensured-created on first resolution.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddPromptRegistry(
        this IServiceCollection services,
        string promptsRootPath,
        PromptUsageOptions usageOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptsRootPath);
        ArgumentNullException.ThrowIfNull(usageOptions);

        services.AddSingleton<IPromptRegistry>(sp =>
            new FilePromptRegistry(
                promptsRootPath,
                sp.GetRequiredService<ILogger<FilePromptRegistry>>()));

        services.AddSingleton<IPromptRenderer, ScribanPromptRenderer>();

        // Request-scoped accumulator for MediatR's PromptUsageTrackingBehavior. Scoped so
        // every request gets a fresh bag; cross-request state never leaks. Services that
        // record directly (e.g. ConversationFactExtractor) do NOT touch the bag — they call
        // IPromptUsageRecorder themselves and stay out of the auto-record pipeline.
        services.AddScoped<IPromptUsageBag, InMemoryPromptUsageBag>();

        if (!usageOptions.PersistenceEnabled)
        {
            // Default path: OTel-only recorder. Zero infrastructure dependencies.
            services.AddSingleton<IPromptUsageRecorder, OtelPromptUsageRecorder>();

            // No durable store in this branch, but the globally-scanned MediatR handlers that
            // query prompt usage (version comparison, trace replay) are always registered.
            // Register a No-op store so they stay constructible (audit item H2 / ValidateOnBuild);
            // empty query results are the correct semantic when nothing is persisted. The real
            // EfCorePromptUsageStore is registered instead in the persistence-enabled branch below.
            services.AddSingleton<
                Application.AI.Common.Prompts.Interfaces.IPromptUsageStore,
                Application.AI.Common.Prompts.NullPromptUsageStore>();
            return services;
        }

        // Persistence enabled: register the DbContext factory, the store, the persistence
        // recorder, and the composite that fans out to both Otel + Persistence.
        services.AddDbContextFactory<PromptUsageDbContext>(opts =>
            opts.UseSqlite(usageOptions.ConnectionString));

        services.AddSingleton<IPromptUsageStore, EfCorePromptUsageStore>();

        // Ensure the schema is created the first time the store is needed. Idempotent.
        // SchemaInitializer<TContext> is the shared base — same lifecycle used by
        // EvalDashboard's persistence registration so future subsystems get the
        // pattern for free.
        services.AddSingleton<SchemaInitializer<PromptUsageDbContext>>();

        // Register the two inner recorders as concrete singletons, then build the composite
        // that fans out to both. The public IPromptUsageRecorder is the composite.
        services.AddSingleton<OtelPromptUsageRecorder>();
        services.AddSingleton<PersistencePromptUsageRecorder>();
        services.AddSingleton<IPromptUsageRecorder>(sp =>
        {
            // Touch the initializer so the DB is migrated before first record.
            _ = sp.GetRequiredService<SchemaInitializer<PromptUsageDbContext>>();
            return new CompositePromptUsageRecorder(
                inner: [sp.GetRequiredService<OtelPromptUsageRecorder>(),
                        sp.GetRequiredService<PersistencePromptUsageRecorder>()],
                logger: sp.GetRequiredService<ILogger<CompositePromptUsageRecorder>>());
        });

        return services;
    }
}
