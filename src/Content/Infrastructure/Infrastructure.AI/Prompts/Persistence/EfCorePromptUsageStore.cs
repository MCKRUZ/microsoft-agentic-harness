using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using Infrastructure.AI.Extensions;
using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Prompts.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IPromptUsageStore"/> over
/// <see cref="PromptUsageDbContext"/>. Uses <see cref="IDbContextFactory{TContext}"/>
/// for short-lived contexts so the store can be a singleton without leaking a
/// shared DbContext across threads.
/// </summary>
[PersonalDataStore(
    "The prompt-usage SQLite table: each rendered prompt's name, version and hash with trace ids and a case id that fact extraction sets to {conversationId}:{turn}. No owner column; operational telemetry, so not exported. Nothing ages rows out; owner erasure removes the rows of the person's conversations.",
    PersonalDataCategories.Identifiers,
    PersonalDataKey.Unkeyed,
    RetentionBasis.Unbounded,
    ErasureSupport.PurgeByOwner,
    ExportSupport.NotApplicable)]
public sealed class EfCorePromptUsageStore : IPromptUsageStore, IPersonalDataEraser, IPersonalDataScrubber
{
    private readonly IDbContextFactory<PromptUsageDbContext> _contextFactory;

    /// <inheritdoc />
    public string ErasureStoreName => "prompt usage records";

    /// <inheritdoc />
    public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.NamedConversations;

    /// <inheritdoc />
    /// <remarks>
    /// Fact extraction writes the case id as <c>{conversationId}:{turn}</c>. A row belongs to one of the person's
    /// conversations only when its case id is that conversation id, a colon, and a turn number; a bare prefix match would
    /// also take <c>conv-1:…</c> rows when erasing <c>conv</c>, or rows of a conversation whose own id contains a colon.
    /// Candidates are narrowed by prefix in the database and confirmed exactly here.
    /// </remarks>
    public async Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.ConversationIds.Count == 0)
            return 0;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var rowIds = new List<long>();
        foreach (var conversationId in subject.ConversationIds)
        {
            var prefix = conversationId + ":";
            var candidates = await context.PromptUsages
                .Where(e => e.CaseId != null && e.CaseId.StartsWith(prefix))
                .Select(e => new { e.Id, e.CaseId })
                .ToListAsync(cancellationToken);
            // The database narrows with LIKE, which SQLite matches case-insensitively, so the prefix is re-checked here
            // exactly: "CONV:7" must survive erasing "conv".
            rowIds.AddRange(candidates
                .Where(c => c.CaseId!.StartsWith(prefix, StringComparison.Ordinal)
                    && c.CaseId.Length > prefix.Length
                    && c.CaseId[prefix.Length..].All(char.IsAsciiDigit))
                .Select(c => c.Id));
        }

        return rowIds.Count == 0
            ? 0
            : await context.PromptUsages.Where(e => rowIds.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ScrubAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.TruncateWriteAheadLogAsync(cancellationToken);
    }

    /// <summary>Initializes a new instance.</summary>
    /// <param name="contextFactory">Creates the short-lived contexts each operation uses.</param>
    /// <param name="schemaInitializer">
    /// Taken so the schema exists before any operation, not only once a recorder has been resolved. Without it, an
    /// erasure or a usage query on a fresh database failed with "no such table", and a failed erasure holds the person's
    /// conversations back (#900).
    /// </param>
    public EfCorePromptUsageStore(
        IDbContextFactory<PromptUsageDbContext> contextFactory,
        SchemaInitializer<PromptUsageDbContext> schemaInitializer)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(schemaInitializer);
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task AppendAsync(PromptUsageRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.PromptUsages.Add(new PromptUsageEntity
        {
            PromptName = record.Descriptor.Name,
            PromptVersionMajor = record.Descriptor.Version.Major,
            PromptVersionMinor = record.Descriptor.Version.Minor,
            PromptHash = record.Descriptor.ContentHash,
            TraceId = record.TraceId,
            SpanId = record.SpanId,
            CaseId = record.CaseId,
            MetricKey = record.MetricKey,
            RecordedAtUtc = record.RecordedAtUtc,
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptUsageRecord>> QueryByTraceIdAsync(string traceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.PromptUsages
            .AsNoTracking()
            .Where(e => e.TraceId == traceId)
            .OrderBy(e => e.RecordedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ConvertAll(ToRecord);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptUsageRecord>> QueryByCaseIdAsync(string caseId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.PromptUsages
            .AsNoTracking()
            .Where(e => e.CaseId == caseId)
            .OrderBy(e => e.RecordedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ConvertAll(ToRecord);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptUsageRecord>> QueryByPromptNameAsync(
        string promptName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptName);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Lowercase equality avoids LIKE-pattern wildcards (%, _) leaking through
        // user-supplied prompt names. EF Core translates ToLower() to SQL lower()
        // which is ASCII-correct on SQLite TEXT — prompt names are kebab-case slugs
        // so non-ASCII handling is not a concern here.
        var lowered = promptName.ToLowerInvariant();
        var rows = await context.PromptUsages
            .AsNoTracking()
            .Where(e => e.PromptName.ToLower() == lowered)
            .OrderBy(e => e.RecordedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ConvertAll(ToRecord);
    }

    private static PromptUsageRecord ToRecord(PromptUsageEntity e) => new()
    {
        Descriptor = new PromptDescriptor
        {
            Name = e.PromptName,
            Version = new PromptVersion(e.PromptVersionMajor, e.PromptVersionMinor),
            ContentHash = e.PromptHash,
            // Body is the immutable on-disk content per (name, version) — not stored
            // in usage rows. Trace-replay re-resolves the descriptor from the registry
            // (or rejects the replay if the version has been deleted).
            Body = string.Empty,
        },
        CaseId = e.CaseId,
        MetricKey = e.MetricKey,
        TraceId = e.TraceId,
        SpanId = e.SpanId,
        RecordedAtUtc = e.RecordedAtUtc,
    };
}
