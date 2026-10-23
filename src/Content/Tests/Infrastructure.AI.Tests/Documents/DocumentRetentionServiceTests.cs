using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.RAG;
using Application.Core.Compliance;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Models;
using FluentAssertions;
using Infrastructure.AI.Documents;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Tests.BackgroundServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Documents;

/// <summary>
/// The optional document age limit (#901) on its schedule: off unless switched on; when on, a document not loaded for the
/// period is removed through the audited removal, recorded as retention, and one loaded more recently is kept.
/// </summary>
public sealed class DocumentRetentionServiceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Origin = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ThreeYears = TimeSpan.FromDays(1095);

    private readonly ObservableFakeClock _clock = new(Origin);
    private readonly AppConfig _config = new();
    private readonly SqliteConnection _keepAlive;
    private readonly Factory _factory;
    private readonly Mock<IErasureOrchestrator> _orchestrator = new();
    private readonly ConcurrentQueue<AuditEntry> _records = new();
    private readonly ConcurrentQueue<string> _removed = new();
    private ErasureCompleteness _completeness = ErasureCompleteness.Full;
    private bool _loadDuringRemoval;

    public DocumentRetentionServiceTests()
    {
        var connectionString = $"Data Source=file:documentretention-{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();
        _factory = new Factory(new DbContextOptionsBuilder<DocumentLoadDbContext>().UseSqlite(connectionString).Options);
        _config.AI.Rag.Documents.Retention.SweepInterval = TimeSpan.FromHours(1);

        // In-process stores, so the shipped SQLite ledger may run the age limit (see DocumentRetentionSafety).
        _config.AI.Rag.VectorStore.Provider = "faiss";

        _orchestrator.Setup(o => o.RemoveDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string id, CancellationToken _) =>
            {
                _removed.Enqueue(id);
                if (_loadDuringRemoval)
                    await LoadAsync(id, _clock.GetUtcNow());
                return Receipt(_completeness);
            });
    }

    /// <inheritdoc />
    public void Dispose() => _keepAlive.Dispose();

    [Fact]
    public async Task ByDefault_TheAgeLimitIsOff_AndRemovesNothing()
    {
        _config.AI.Rag.Documents.Retention.Enabled.Should().BeFalse("loaded documents are kept until removed unless an organisation chooses a limit");
        await LoadAsync("0000000000000001", Origin - TimeSpan.FromDays(4000));
        using var service = CreateService(Ledger(Origin));
        await StartAsync(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _removed.Should().BeEmpty();
    }

    [Fact]
    public async Task SwitchedOn_RemovesDocumentsPastThePeriod_AsRetention_AndKeepsRecentOnes()
    {
        _config.AI.Rag.Documents.Retention.Enabled = true;
        _config.AI.Rag.Documents.Retention.RetainFor = ThreeYears;
        await LoadAsync("00000000000000AA", Origin - ThreeYears - TimeSpan.FromDays(1));
        await LoadAsync("00000000000000BB", Origin - ThreeYears + TimeSpan.FromDays(1));
        using var service = CreateService(Ledger(Origin));
        await StartAsync(service);

        _removed.Should().BeEmpty("control: nothing is removed before a tick");
        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _removed.Should().Equal("00000000000000AA");
        (await Ledger(Origin).GetLastLoadedAsync("00000000000000AA", CancellationToken.None)).Should().BeNull();
        (await Ledger(Origin).GetLastLoadedAsync("00000000000000BB", CancellationToken.None)).Should().NotBeNull();
        _records.Should().NotBeEmpty().And.OnlyContain(r =>
            r.ExecutorId == ErasureActor.RetentionExecutor && r.Metadata!["initiator"] == "retention"
            && r.Metadata["document_id"] == "00000000000000AA");
    }

    [Fact]
    public async Task APartialRemoval_KeepsTheRecord_AndIsTriedAgainOnTheNextSweep()
    {
        _config.AI.Rag.Documents.Retention.Enabled = true;
        _completeness = ErasureCompleteness.Partial;
        await LoadAsync("00000000000000AA", Origin - TimeSpan.FromDays(4000));
        using var service = CreateService(Ledger(Origin));
        await StartAsync(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));
        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _removed.Should().Equal("00000000000000AA", "00000000000000AA");
    }

    [Fact]
    public async Task ADocumentLoadedAgainSinceTheListWasRead_IsKept()
    {
        _config.AI.Rag.Documents.Retention.Enabled = true;
        var ledger = new Mock<IDocumentLoadLedger>();
        ledger.Setup(l => l.ListLoadedBeforeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["00000000000000AA"]);
        ledger.Setup(l => l.GetLastLoadedAsync("00000000000000AA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _clock.GetUtcNow());
        using var service = CreateService(ledger.Object);
        await StartAsync(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _removed.Should().BeEmpty();
    }

    [Fact]
    public async Task ADocumentLoadedAgainWhileItIsBeingRemoved_KeepsItsNewLoadTime()
    {
        // The load lands after the sweep judged the document old: its record must survive, or the copy it indexed could
        // never age out.
        _config.AI.Rag.Documents.Retention.Enabled = true;
        _loadDuringRemoval = true;
        await LoadAsync("00000000000000AA", Origin - TimeSpan.FromDays(4000));
        using var service = CreateService(Ledger(Origin));
        await StartAsync(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _removed.Should().Equal("00000000000000AA");
        (await Ledger(Origin).GetLastLoadedAsync("00000000000000AA", CancellationToken.None))
            .Should().Be(Origin + TimeSpan.FromHours(1));
    }

    [Fact]
    public void SwitchedOnOverSharedStoresWithTheLocalLedger_StopsTheHost()
    {
        _config.AI.Rag.Documents.Retention.Enabled = true;
        _config.AI.Rag.VectorStore.Provider = "azure_ai_search";

        var act = () => CreateService(Ledger(Origin));

        act.Should().Throw<InvalidOperationException>().WithMessage("*LoadLedgerSharedByEveryWriter*");
    }

    [Fact]
    public async Task SwitchedOnOverSharedStoresWithoutARestart_RemovesNothing()
    {
        // A reload is not re-checked by the constructor, so the sweep itself must refuse.
        await LoadAsync("00000000000000AA", Origin - TimeSpan.FromDays(4000));
        using var service = CreateService(Ledger(Origin));
        await StartAsync(service);
        _config.AI.Rag.Documents.Retention.Enabled = true;
        _config.AI.Rag.VectorStore.Provider = "azure_ai_search";

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _removed.Should().BeEmpty();
    }

    [Fact]
    public async Task APeriodUnderADay_RemovesNothing()
    {
        // A reload is not re-validated, so a mistyped period must be refused by the sweep itself.
        _config.AI.Rag.Documents.Retention.Enabled = true;
        _config.AI.Rag.Documents.Retention.RetainFor = TimeSpan.FromHours(23);
        await LoadAsync("00000000000000AA", Origin - TimeSpan.FromDays(2));
        using var service = CreateService(Ledger(Origin));
        await StartAsync(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _removed.Should().BeEmpty();
    }

    [Fact]
    public async Task OneSweep_RemovesAtMostTheCap_OldestFirst()
    {
        _config.AI.Rag.Documents.Retention.Enabled = true;
        var total = DocumentRetentionService.MaxDocumentsPerSweep + 5;
        for (var i = 0; i < total; i++)
            await LoadAsync(i.ToString("X16"), Origin - TimeSpan.FromDays(4000) + TimeSpan.FromMinutes(i));
        using var service = CreateService(Ledger(Origin));
        await StartAsync(service);

        await AdvanceAndSettle(TimeSpan.FromHours(1));

        _removed.Should().HaveCount(DocumentRetentionService.MaxDocumentsPerSweep);
        _removed.First().Should().Be(0.ToString("X16"));
    }

    private DocumentRetentionService CreateService(IDocumentLoadLedger ledger)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(_orchestrator.Object);
        services.AddSingleton<IAuditSink>(new CapturingSink(_records));
        services.AddSingleton<IDocumentLoadLedger>(Ledger(Origin));
        services.AddScoped<AuditedDocumentRemoval>();
        var provider = services.BuildServiceProvider();

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(() => _config);
        return new DocumentRetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(), ledger, monitor.Object, _clock,
            NullLogger<DocumentRetentionService>.Instance);
    }

    private EfCoreDocumentLoadLedger Ledger(DateTimeOffset now) =>
        new(_factory, new SchemaInitializer<DocumentLoadDbContext>(_factory), new FakeTimeProvider(now));

    private Task LoadAsync(string documentId, DateTimeOffset at) =>
        Ledger(at).RecordLoadedAsync(documentId, CancellationToken.None);

    private async Task StartAsync(DocumentRetentionService service)
    {
        var armed = _clock.NextTimer;
        await service.StartAsync(CancellationToken.None);
        await armed.WaitAsync(Patience);
    }

    private async Task AdvanceAndSettle(TimeSpan by)
    {
        var reArmed = _clock.NextTimer;
        _clock.Advance(by);
        await reArmed.WaitAsync(Patience);
    }

    private static ErasureReceipt Receipt(ErasureCompleteness completeness) => new()
    {
        RequestId = Guid.NewGuid().ToString(),
        ScopeId = "document",
        RequestedAt = Origin,
        CompletedAt = Origin,
        NodesDeleted = 0,
        EdgesDeleted = 0,
        FeedbackWeightsDeleted = 0,
        VectorEmbeddingsDeleted = 1,
        Completeness = completeness,
    };

    private sealed class CapturingSink(ConcurrentQueue<AuditEntry> records) : IAuditSink
    {
        public ValueTask RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry.Action == AuditedDocumentRemoval.AuditAction)
                records.Enqueue(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Factory(DbContextOptions<DocumentLoadDbContext> options) : IDbContextFactory<DocumentLoadDbContext>
    {
        public DocumentLoadDbContext CreateDbContext() => new(options);
    }
}
