using Application.AI.Common.Evaluation.Interfaces;
using Domain.Common.Config.AI;
using Infrastructure.AI.Evaluation.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI.Evaluation;

/// <summary>
/// DI registrations for the eval dashboard persistence subsystem (Sub-phase 5.4.1).
/// </summary>
/// <remarks>
/// Lives in a dedicated extension type rather than <see cref="DependencyInjection"/>
/// so consumers who don't want the dashboard (zero-infra default) don't transitively
/// register EF Core. Mirrors the <c>PromptRegistryDependencyInjection</c> shape.
/// </remarks>
public static class EvalDashboardDependencyInjection
{
    /// <summary>
    /// Registers <see cref="IEvalRunStore"/> against
    /// <see cref="EvalDashboardOptions.PersistenceEnabled"/>. When persistence is
    /// enabled, registers the SQLite-backed <see cref="EfCoreEvalRunStore"/>; when
    /// disabled, registers <see cref="NullEvalRunStore"/> so callers can resolve the
    /// interface unconditionally. Mirrors the prompt-registry recorder pattern.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">
    /// Persistence configuration. When enabled, connection string is honoured verbatim
    /// and the schema is ensured-created on first resolution via the registered
    /// initializer singleton.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddEvalDashboardPersistence(
        this IServiceCollection services,
        EvalDashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.PersistenceEnabled)
        {
            // Default surface: no-op store. Lets the Step 5.4.2 ingest handler resolve
            // IEvalRunStore unconditionally — the host opts into real persistence by
            // flipping PersistenceEnabled, no other wiring change required.
            services.AddSingleton<IEvalRunStore, NullEvalRunStore>();
            return services;
        }

        services.AddDbContextFactory<EvalDashboardDbContext>(opts =>
            opts.UseSqlite(options.ConnectionString));

        // Ensure the schema exists the first time the store is needed. Idempotent.
        services.AddSingleton<EvalDashboardSchemaInitializer>();

        // Factory wraps the store so resolving IEvalRunStore touches the initializer first,
        // guaranteeing schema-create runs exactly once before any AppendAsync hits the DB.
        services.AddSingleton<IEvalRunStore>(sp =>
        {
            _ = sp.GetRequiredService<EvalDashboardSchemaInitializer>();
            return new EfCoreEvalRunStore(
                sp.GetRequiredService<IDbContextFactory<EvalDashboardDbContext>>());
        });

        return services;
    }
}

/// <summary>
/// Singleton initializer that ensures the eval dashboard SQLite schema exists.
/// Resolved at composition time so the first append never races a missing-table error.
/// </summary>
internal sealed class EvalDashboardSchemaInitializer
{
    /// <summary>Initializes a new instance and ensures the database is created.</summary>
    public EvalDashboardSchemaInitializer(IDbContextFactory<EvalDashboardDbContext> contextFactory)
    {
        using var context = contextFactory.CreateDbContext();
        context.Database.EnsureCreated();
    }
}
