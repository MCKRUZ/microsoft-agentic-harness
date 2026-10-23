using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.Services.Compliance;
using Domain.Common.Config;
using Infrastructure.AI.Documents;
using Infrastructure.AI.Extensions;
using Infrastructure.AI.Helpers;
using Infrastructure.AI.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        // The erasure receipt says that documents other people loaded may still mention the person, and why they are kept;
        // with the age limit running, how long. Read per request, so a reloaded setting is reported as it now stands.
        services.AddScoped(sp => RetainedPersonalData.BusinessContent(
            "documents other people loaded", RunningAgeLimit(sp)));
    }

    /// <summary>The document age limit when the sweep would actually run it, or <see langword="null"/>.</summary>
    private static TimeSpan? RunningAgeLimit(IServiceProvider services)
    {
        var config = services.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;
        var retention = config.AI.Rag.Documents.Retention;
        var runs = retention.Enabled
            && retention.RetainFor >= RetentionPeriodConfig.MinimumRetainFor
            && DocumentRetentionSafety.Refusal(config, services.GetRequiredService<IDocumentLoadLedger>()) is null;
        return runs ? retention.RetainFor : null;
    }
}
