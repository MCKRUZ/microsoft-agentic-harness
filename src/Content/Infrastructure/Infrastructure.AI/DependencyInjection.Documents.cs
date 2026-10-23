using Application.AI.Common.Interfaces.RAG;
using Domain.Common.Config;
using Infrastructure.AI.Documents;
using Infrastructure.AI.Extensions;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the document load record and the optional document age limit (#901). The record is always kept, so
    /// switching the limit on later reaches every document loaded since; the sweep runs only when
    /// <c>AppConfig:AI:Rag:Documents:Retention:Enabled</c> is set.
    /// </summary>
    private static void RegisterDocumentRetention(IServiceCollection services, AppConfig appConfig)
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, appConfig.AI.Rag.Documents.LoadLedgerDatabasePath);
        OwnerOnlyDirectoryHelper.Create(Path.GetDirectoryName(dbPath)!);

        services.AddDbContextFactory<DocumentLoadDbContext>(options => options.UseSqliteWithSecureDelete($"DataSource={dbPath}"));
        services.AddSingleton<SchemaInitializer<DocumentLoadDbContext>>();
        services.AddSingleton<IDocumentLoadLedger, EfCoreDocumentLoadLedger>();
        services.AddHostedService<DocumentRetentionService>();
    }
}
