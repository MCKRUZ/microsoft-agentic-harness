using System.Reflection;
using System.Text.Json;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Compliance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Compliance;

/// <summary>
/// Makes a copy of one person's data from every registered <see cref="IPersonalDataExporter"/>, with a manifest that names
/// every other store the host runs that can hold their data and why it returned nothing (#902).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The manifest covers what erasure covers.</strong> Its lines come from the same registrations the erasure receipt
/// reads: each store that can erase one person (<see cref="IPersonalDataEraser"/>) and each store that keeps personal data
/// it cannot search (<see cref="RetainedPersonalData"/>). A store on either list that does not export is named, so the copy
/// never reads as complete while one of them holds the person's data. Cross-session memory, erased through the knowledge
/// graph rather than as its own eraser, is named the same way.
/// </para>
/// <para>
/// <strong>Runs system-scoped, one request per person.</strong> The ambient request scope is suspended, as for erasure, so
/// a scope-filtering store answers for the subject rather than for whoever is asking; each exporter filters on the
/// subject's id itself. Deciding who may be exported is the caller's job. The erasure journal's per-person turn is held for
/// the request, so a copy is never made from data an erasure for the same person is halfway through deleting.
/// </para>
/// <para>
/// <strong>Failures are contained per store.</strong> One store throwing is a <see cref="ExportStatus.Failed"/> line with a
/// fixed phrase; the exception goes to the log with its message withheld, because store exceptions can carry paths and
/// connection strings, and the other stores still answer.
/// </para>
/// </remarks>
public sealed class PersonalDataExportCoordinator : IPersonalDataExport
{
    private const string FailedReason = "the store could not be read; the failure has been logged";
    private const string NotExportableReason = "this store can hold your data but cannot yet return a copy of it";
    private const string NotApplicableReason = "holds nothing a person would receive a copy of";
    private const string UnreadableReason =
        "some stored conversations could not be read, so whether they are yours is unknown; they were left out";
    private const string ConversationListStore = "conversation list";
    private const string PlanRunListStore = "planned-task run list";
    private const string JournalStore = "unfinished erasure requests";
    private const string UnreadableStore = "conversations that could not be read";
    private const string MemoryStore = "cross-session memories";

    private readonly IReadOnlyList<IPersonalDataExporter> _exporters;
    private readonly IReadOnlyList<IPersonalDataEraser> _erasers;
    private readonly IReadOnlyList<RetainedPersonalData> _retained;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PersonalDataExportCoordinator> _logger;
    private readonly IConversationStore? _conversations;
    private readonly IErasureJournal? _journal;
    private readonly ICrossSessionMemoryStore? _memory;
    private readonly IPlanRunTraceSource? _planRuns;

    /// <summary>Initializes a new instance of the <see cref="PersonalDataExportCoordinator"/> class.</summary>
    /// <param name="exporters">Every registered store that can return one person's data.</param>
    /// <param name="erasers">Every registered store that can erase one person; those that cannot export are named.</param>
    /// <param name="retained">Every registered store that keeps personal data it cannot search for one person.</param>
    /// <param name="ambientScope">Suspended for the request, so scope-filtering stores see the subject's data.</param>
    /// <param name="timeProvider">Time source for when the copy was made.</param>
    /// <param name="logger">Logger for store failures, which never reach the manifest.</param>
    /// <param name="conversations">
    /// The conversation store, read for the subject's conversation ids. <see langword="null"/> in a host without one.
    /// </param>
    /// <param name="journal">Supplies the per-person turn shared with erasure. <see langword="null"/> in a host without one.</param>
    /// <param name="memory">Cross-session memory, named when it cannot export. <see langword="null"/> when not deployed.</param>
    /// <param name="planRuns">
    /// The plan store's record of the person's planned-task runs, the same source erasure reads. <see langword="null"/> in
    /// a host without a planner.
    /// </param>
    public PersonalDataExportCoordinator(
        IEnumerable<IPersonalDataExporter> exporters,
        IEnumerable<IPersonalDataEraser> erasers,
        IEnumerable<RetainedPersonalData> retained,
        IAmbientRequestScope ambientScope,
        TimeProvider timeProvider,
        ILogger<PersonalDataExportCoordinator> logger,
        IConversationStore? conversations = null,
        IErasureJournal? journal = null,
        ICrossSessionMemoryStore? memory = null,
        IPlanRunTraceSource? planRuns = null)
    {
        ArgumentNullException.ThrowIfNull(exporters);
        ArgumentNullException.ThrowIfNull(erasers);
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        // A forwarded registration yields null when the host's store cannot export or erase; see AddPersonalDataExporter.
        // A substrate composed twice registers each store twice: one store is still one section and one line (#1004).
        _exporters = PersonalDataRegistrations.DistinctExporters(exporters);
        _erasers = PersonalDataRegistrations.DistinctErasers(erasers);
        _retained = retained.ToList();
        _ambientScope = ambientScope;
        _timeProvider = timeProvider;
        _logger = logger;
        _conversations = conversations;
        _journal = journal;
        _memory = memory;
        _planRuns = planRuns;
    }

    /// <inheritdoc />
    public async Task<PersonalDataExport> ExportOwnerAsync(string ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        using var systemScoped = _ambientScope.SuspendScope();
        using var turn = _journal is null ? null : await _journal.BeginRequestAsync(ownerId, cancellationToken);

        var manifest = new List<ExportManifestEntry>();
        var subject = await ResolveSubjectAsync(ownerId, manifest, cancellationToken);

        var sections = new List<ExportSection>();
        foreach (var exporter in DistinctByStore(_exporters))
        {
            var records = await ExportAsync(exporter, subject, manifest, cancellationToken);
            if (records is not null)
                sections.Add(new ExportSection(exporter.ExportStoreName, records));
        }

        NameStoresThatDidNotExport(manifest);
        return new PersonalDataExport(Guid.NewGuid().ToString("N"), _timeProvider.GetUtcNow(), manifest, sections);
    }

    /// <summary>
    /// One exporter per physical store. Two registrations can front one store (Azure AI Search backs the vector and BM25
    /// stores with one index); they say so with the same <see cref="IPersonalDataExporter.SharedStoreKey"/>, and reading it
    /// once keeps the person's records from appearing twice. Both still count as exported, so neither is named as a store
    /// the copy leaves out. Exporters without a key are all read, even under one name, so none is dropped by a coincidence.
    /// </summary>
    private static IEnumerable<IPersonalDataExporter> DistinctByStore(IReadOnlyList<IPersonalDataExporter> exporters)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return exporters.Where(e => e.SharedStoreKey is not { } key || seen.Add(key));
    }

    /// <summary>
    /// Every id that leads to the subject's data, gathered as erasure gathers them: their conversations, their planned-task
    /// runs, and what an unfinished erasure request recorded (#907), whose conversations or plans may already be gone while
    /// the stores they led to still hold data.
    /// </summary>
    private async Task<ExportSubject> ResolveSubjectAsync(
        string ownerId, List<ExportManifestEntry> manifest, CancellationToken cancellationToken)
    {
        var conversations = await ListConversationIdsAsync(ownerId, manifest, cancellationToken);
        var planRuns = await ReadOrFailAsync(
            _planRuns is null ? null : ct => _planRuns.ListRunTracesAsync(ownerId, ct),
            PlanRunTraces.Empty, PlanRunListStore, manifest, cancellationToken);
        var journaled = await ReadOrFailAsync(
            _journal is null ? null : ct => _journal.ReadAsync(ownerId, ct),
            ErasureJournalEntries.Empty, JournalStore, manifest, cancellationToken);

        return new ExportSubject(
            ownerId,
            [.. conversations.Union(journaled.ConversationIds, StringComparer.Ordinal)],
            journaled.PlanRuns.IsEmpty ? planRuns : planRuns.Union(journaled.PlanRuns));
    }

    /// <summary>
    /// Reads one source of the subject's ids. A failure leaves out what only it leads to, so it is a manifest line rather
    /// than a silently smaller copy.
    /// </summary>
    private async Task<T> ReadOrFailAsync<T>(
        Func<CancellationToken, Task<T>>? read, T none, string store, List<ExportManifestEntry> manifest,
        CancellationToken cancellationToken)
    {
        if (read is null)
            return none;

        try
        {
            return await read(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Export could not read the {Store}.", store);
            manifest.Add(new ExportManifestEntry(store, ExportStatus.Failed, 0, FailedReason));
            return none;
        }
    }

    /// <summary>
    /// The subject's conversation ids. Stores that record no owner are reached only through them, so a failure here, or
    /// conversations that could not be read, is a manifest line rather than a silently smaller copy.
    /// </summary>
    private async Task<IReadOnlyList<string>> ListConversationIdsAsync(
        string ownerId, List<ExportManifestEntry> manifest, CancellationToken cancellationToken)
    {
        try
        {
            var listing = _conversations switch
            {
                null => ConversationIdListing.Empty,
                IConversationIdSource source => await source.ListConversationIdsAsync(ownerId, cancellationToken),
                var store => new ConversationIdListing(
                    [.. (await store.ListAsync(ownerId, cancellationToken)).Select(c => c.Id)], 0),
            };
            if (listing.Unreadable > 0)
                manifest.Add(new ExportManifestEntry(UnreadableStore, ExportStatus.Failed, 0, UnreadableReason));
            return listing.Ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Export could not list the subject's conversations.");
            manifest.Add(new ExportManifestEntry(ConversationListStore, ExportStatus.Failed, 0, FailedReason));
            return [];
        }
    }

    private async Task<IReadOnlyList<JsonElement>?> ExportAsync(
        IPersonalDataExporter exporter, ExportSubject subject, List<ExportManifestEntry> manifest,
        CancellationToken cancellationToken)
    {
        // Never read: a store with no backend configured has never held anything (#1003).
        if (exporter is IConfigurablePersonalDataStore { IsConfigured: false })
        {
            manifest.Add(new ExportManifestEntry(
                exporter.ExportStoreName, ExportStatus.NotApplicable, 0, IConfigurablePersonalDataStore.NotConfiguredReason));
            return null;
        }

        try
        {
            var records = await exporter.ExportSubjectAsync(subject, cancellationToken);
            manifest.Add(new ExportManifestEntry(exporter.ExportStoreName, ExportStatus.Included, records.Count));
            return records;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(MessageWithheldException.From(ex), "Export from {Store} failed.", exporter.ExportStoreName);
            manifest.Add(new ExportManifestEntry(exporter.ExportStoreName, ExportStatus.Failed, 0, FailedReason));
            return null;
        }
    }

    /// <summary>Names every store erasure would reach or list that this copy does not include, and why.</summary>
    private void NameStoresThatDidNotExport(List<ExportManifestEntry> manifest)
    {
        foreach (var eraser in _erasers.Where(e => !IsExported(e)))
        {
            manifest.Add(eraser is IConfigurablePersonalDataStore { IsConfigured: false }
                ? new ExportManifestEntry(
                    eraser.ErasureStoreName, ExportStatus.NotApplicable, 0, IConfigurablePersonalDataStore.NotConfiguredReason)
                : NotExported(eraser.ErasureStoreName, eraser is NonErasableStore ? null : eraser.GetType()));
        }

        // Named unless it was actually read: a memory store that can export but was never registered as an exporter would
        // otherwise be left out of the copy without a line saying so.
        if (_memory is not null && !_exporters.Any(e => ReferenceEquals(e, _memory)))
            manifest.Add(NotExported(MemoryStore, _memory.GetType()));

        foreach (var outcome in _retained.Select(r => r.ToOutcome()).OfType<StoreErasureOutcome>()
                     .DistinctBy(o => o.Store, StringComparer.Ordinal))
            manifest.Add(new ExportManifestEntry(outcome.Store, Retained(outcome.Status), 0, outcome.Reason));
    }

    /// <summary>
    /// Whether <paramref name="eraser"/> is a store that also exported. A stand-in for a store that cannot erase carries the
    /// store's type name, so it is matched to an exporter of that type.
    /// </summary>
    private bool IsExported(IPersonalDataEraser eraser) =>
        _exporters.Any(x => ReferenceEquals(x, eraser)
            || (eraser is NonErasableStore && x.GetType().Name == eraser.ErasureStoreName));

    private static ExportManifestEntry NotExported(string store, Type? storeType) =>
        storeType?.GetCustomAttribute<PersonalDataStoreAttribute>()?.Export == ExportSupport.NotApplicable
            ? new ExportManifestEntry(store, ExportStatus.NotApplicable, 0, NotApplicableReason)
            : new ExportManifestEntry(store, ExportStatus.NotExportable, 0, NotExportableReason);

    private static ExportStatus Retained(StoreErasureStatus status) => status switch
    {
        StoreErasureStatus.RetainedUntilExpiry => ExportStatus.RetainedUntilExpiry,
        StoreErasureStatus.RetainedByObligation => ExportStatus.RetainedByObligation,
        StoreErasureStatus.RetainedAsBusinessContent => ExportStatus.RetainedAsBusinessContent,
        StoreErasureStatus.RetainedIndefinitely => ExportStatus.RetainedUnsearchable,
        // Any other status a retained store reports reads as data left out: the copy stays honest without failing.
        _ => ExportStatus.RetainedUnsearchable,
    };
}
