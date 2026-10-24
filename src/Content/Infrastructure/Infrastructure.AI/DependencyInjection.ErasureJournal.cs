using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Config;
using Infrastructure.AI.Compliance;
using Infrastructure.AI.Extensions;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Persistence;
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
        services.AddSingleton<EfCoreErasureJournal>();
        services.AddSingleton<IErasureJournal>(sp => sp.GetRequiredService<EfCoreErasureJournal>());
        services.AddHostedService<ErasureJournalRetentionService>();
    }
}
