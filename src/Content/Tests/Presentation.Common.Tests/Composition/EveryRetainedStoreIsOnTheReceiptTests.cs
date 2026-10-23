using System.Reflection;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Services.Compliance;
using Domain.Common.Compliance;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// In the real composition, every store the host resolves that keeps personal data it cannot search for one person (logs,
/// traces, the response cache, the audit trails) is listed on the erasure receipt (#900).
/// </summary>
/// <remarks>
/// <para>
/// A store missing from the list is invisible to the person reading their receipt, and one kept forever would leave the
/// receipt reading <c>Full</c> while it still holds their data. This asks the composition itself rather than a list: for
/// every service a retained-store type can be registered as, it resolves what the host actually uses, and requires every
/// such instance to be one the coordinator's <see cref="RetainedPersonalData"/> registrations wrap.
/// </para>
/// <para>
/// A retained-store type is one whose <c>[PersonalDataStore]</c> descriptor says it cannot erase one person
/// (<see cref="ErasureSupport.NotSupported"/>, <see cref="ErasureSupport.ItemDeleteOnly"/> or
/// <see cref="ErasureSupport.Exempt"/>), that is not keyed to the owner (those appear as <c>NotSupported</c> through their
/// erasure registration), and that does not erase through <see cref="IPersonalDataEraser"/>.
/// </para>
/// </remarks>
public sealed class EveryRetainedStoreIsOnTheReceiptTests : IDisposable
{
    private static readonly Type[] NotStoreContracts = [typeof(IDisposable), typeof(IAsyncDisposable)];

    private readonly string _workingDir =
        Path.Combine(Path.GetTempPath(), "composition-retained-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workingDir))
            Directory.Delete(_workingDir, recursive: true);
    }

    [Fact]
    public async Task EveryResolvedRetainedStore_IsListedOnTheReceipt()
    {
        // File logging on, as a deployment with a logs folder runs it; every database in this test's own folder.
        var (services, _) = CompositionRootTestHost.ComposeServices(new Dictionary<string, string?>
        {
            ["AppConfig:Logging:LogsBasePath"] = Path.Combine(_workingDir, "logs"),
            ["AppConfig:Logging:EnableStructuredJson"] = "true",
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:AI:Schedules:DatabasePath"] = Path.Combine(_workingDir, "schedules.db"),
            ["AppConfig:AI:Planner:DatabasePath"] = Path.Combine(_workingDir, "planner.db"),
            ["AppConfig:AI:PromptUsage:ConnectionString"] = $"Data Source={Path.Combine(_workingDir, "prompt-usage.db")}",
            ["AppConfig:AI:ContextManagement:ToolResultStorage:StoragePath"] = Path.Combine(_workingDir, "spills"),
        });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var listed = scope.ServiceProvider.GetServices<RetainedPersonalData>()
            .Where(r => r.ToOutcome() is not null)
            .Select(r => r.Store)
            .ToList();

        // Interfaces only: that is how the rest of the host reaches a store. A concrete registration such as
        // EfCoreChangeProposalStore exists beside its interface and is used only when its feature is switched on, which
        // the interface's resolution already reflects.
        var contracts = services
            .Where(d => !d.IsKeyedService && d.ServiceType.IsInterface && !d.ServiceType.ContainsGenericParameters)
            .Select(d => d.ServiceType)
            .Where(t => !NotStoreContracts.Contains(t) && RetainedStoreTypes.Value.Any(t.IsAssignableFrom))
            .Distinct()
            .ToList();

        var resolved = contracts
            .SelectMany(contract => scope.ServiceProvider.GetServices(contract)
                .Where(instance => instance is not null && IsRetainedStore(instance.GetType()))
                .Select(instance => (Contract: contract, Instance: instance!)))
            .ToList();

        var missing = resolved.Where(r => !listed.Any(l => ReferenceEquals(l, r.Instance)))
            .Select(r => $"{r.Contract.Name} -> {r.Instance.GetType().Name}")
            .Distinct();
        string.Join(Environment.NewLine, missing).Should().BeEmpty("a store the host runs that cannot be searched for one person must be on the receipt; register it with AddRetainedPersonalData");

        // A floor, so this cannot pass on a composition that resolved no retained stores at all.
        resolved.Select(r => r.Instance).Distinct().Should().HaveCountGreaterThanOrEqualTo(5,
            "the default composition runs log files, traces, the response cache and several audit trails");
    }

    [Fact]
    public async Task TheResponseCacheAndDelegationRecords_AreListedWithTheirLivePeriods()
    {
        // #901: both now age out by default, so the receipt says how long, and neither makes it Partial on its own.
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:AI:Orchestration:Subagent:DelegationStoragePath"] = Path.Combine(_workingDir, "delegations"),
        });
        await using var scope = provider.CreateAsyncScope();

        var lines = scope.ServiceProvider.GetServices<RetainedPersonalData>()
            .Select(r => r.ToOutcome())
            .OfType<StoreErasureOutcome>()
            .ToDictionary(o => o.Store);

        lines["chat response cache"].Status.Should().Be(StoreErasureStatus.RetainedUntilExpiry);
        lines["chat response cache"].Reason.Should().EndWith("within 1 hour of being written");
        lines["agent delegation records"].Status.Should().Be(StoreErasureStatus.RetainedUntilExpiry);
        lines["agent delegation records"].Reason.Should().EndWith("within 91 days of being written",
            "90 days after the day's file was last written, which is up to a day after the record");
    }

    [Fact]
    public async Task LogFiles_AreListedWithTheirLivePeriod_AndTheSweepSeesTheProvidersTheLoggersWriteThrough()
    {
        // The retention sweep skips the run a provider is writing by asking the concrete provider; if that were a second
        // instance, it would always say "no run" and the live run would be deleted under the writer.
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:Logging:LogsBasePath"] = Path.Combine(_workingDir, "logs"),
            ["AppConfig:Logging:EnableStructuredJson"] = "true",
        });
        await using var scope = provider.CreateAsyncScope();

        var lines = scope.ServiceProvider.GetServices<RetainedPersonalData>()
            .Select(r => r.ToOutcome())
            .OfType<StoreErasureOutcome>()
            .ToDictionary(o => o.Store);
        foreach (var store in new[] { "application log files", "structured application logs" })
        {
            lines[store].Status.Should().Be(StoreErasureStatus.RetainedUntilExpiry);
            lines[store].Reason.Should().EndWith("within 30 days of being written");
        }

        var writers = provider.GetServices<Microsoft.Extensions.Logging.ILoggerProvider>().ToList();
        writers.Should().Contain(w => ReferenceEquals(w, provider.GetRequiredService<Application.Common.Logging.FileLoggerProvider>()));
        writers.Should().Contain(w => ReferenceEquals(w, provider.GetRequiredService<Application.Common.Logging.StructuredJsonLoggerProvider>()));
    }

    [Fact]
    public async Task SavedWorkflowState_IsListedWithItsLivePeriod()
    {
        // The host reaches both stores only through the composite state manager, so the interface scan above cannot see
        // them; before #901 neither was on the receipt at all.
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:Infrastructure:StateManagement:BasePath"] = Path.Combine(_workingDir, "projects"),
        });
        await using var scope = provider.CreateAsyncScope();

        var lines = scope.ServiceProvider.GetServices<RetainedPersonalData>()
            .Select(r => r.ToOutcome())
            .OfType<StoreErasureOutcome>()
            .ToDictionary(o => o.Store);

        foreach (var store in new[] { "workflow state", "workflow state documents" })
        {
            lines[store].Status.Should().Be(StoreErasureStatus.RetainedUntilExpiry);
            lines[store].Reason.Should().EndWith("within 90 days of being written");
        }
    }

    [Theory]
    [InlineData(false, false, "kept until it removes them")]
    [InlineData(true, true, "or until 1095 days after they were last loaded")]
    [InlineData(true, false, "kept until it removes them")]
    public async Task DocumentsOthersLoaded_AreOnTheReceipt_WithTheAgeLimitOnlyWhenItRuns(
        bool ageLimitOn, bool everyWriterSharesTheRecord, string reasonEnd)
    {
        // #901. The last case: the limit is switched on but refused over shared stores, so the receipt must not claim it.
        await using var provider = CompositionRootTestHost.BuildProvider(new Dictionary<string, string?>
        {
            ["AppConfig:AI:Conversations:DatabasePath"] = Path.Combine(_workingDir, "conversations.db"),
            ["AppConfig:AI:Rag:Documents:Retention:Enabled"] = ageLimitOn.ToString(),
            ["AppConfig:AI:Rag:Documents:LoadLedgerSharedByEveryWriter"] = everyWriterSharesTheRecord.ToString(),
        });
        await using var scope = provider.CreateAsyncScope();

        var line = scope.ServiceProvider.GetServices<RetainedPersonalData>()
            .Select(r => r.ToOutcome())
            .OfType<StoreErasureOutcome>()
            .Should().ContainSingle(o => o.Status == StoreErasureStatus.RetainedAsBusinessContent).Subject;

        line.Store.Should().Be("documents other people loaded");
        line.Reason.Should().EndWith(reasonEnd);
    }

    private static bool IsRetainedStore(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && !typeof(IPersonalDataEraser).IsAssignableFrom(type)
        && type.GetCustomAttribute<PersonalDataStoreAttribute>() is { } d
        && d.KeyedBy != PersonalDataKey.Owner
        && d.Erasure is ErasureSupport.NotSupported or ErasureSupport.ItemDeleteOnly or ErasureSupport.Exempt;

    private static readonly Lazy<IReadOnlyList<Type>> RetainedStoreTypes = new(() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && IsProductionAssembly(a.GetName().Name ?? ""))
        .SelectMany(a => a.GetTypes())
        .Where(IsRetainedStore)
        .ToList());

    private static bool IsProductionAssembly(string name) =>
        !name.Contains(".Tests", StringComparison.Ordinal)
        && (name.StartsWith("Application.", StringComparison.Ordinal)
            || name.StartsWith("Infrastructure.", StringComparison.Ordinal)
            || name.StartsWith("Presentation.", StringComparison.Ordinal));
}
