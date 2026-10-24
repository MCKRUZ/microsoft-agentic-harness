using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Config;
using Infrastructure.AI.Compliance;
using Infrastructure.AI.Extensions;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the erasure journal (#907): the durable record of the ids an erasure request read, which the erasure
    /// coordinator merges into every later request for the same person until every store that reads them has succeeded,
    /// and the hourly sweep that removes rows past <c>AppConfig:AI:Erasure:JournalRetention</c>.
    /// </summary>
    private static void RegisterErasureJournal(IServiceCollection services, AppConfig appConfig)
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, appConfig.AI.Erasure.JournalDatabasePath);
        OwnerOnlyDirectoryHelper.Create(Path.GetDirectoryName(dbPath)!);

        services.AddDbContextFactory<ErasureJournalDbContext>(options => options.UseSqliteWithSecureDelete($"DataSource={dbPath}"));
        services.AddSingleton<SchemaInitializer<ErasureJournalDbContext>>();
        // The schema is created on first use: the conversation stores depend on the journal, and a journal database that
        // cannot open must not make them unresolvable.
        services.AddSingleton(sp => new EfCoreErasureJournal(
            sp.GetRequiredService<IDbContextFactory<ErasureJournalDbContext>>(),
            new Lazy<SchemaInitializer<ErasureJournalDbContext>>(sp.GetRequiredService<SchemaInitializer<ErasureJournalDbContext>>),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IErasureJournal>(sp => sp.GetRequiredService<EfCoreErasureJournal>());
        services.AddHostedService<ErasureJournalRetentionService>();
    }
}
