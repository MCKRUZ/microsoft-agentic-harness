using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.Prompts;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Prompts.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IPromptUsageStore"/> over
/// <see cref="PromptUsageDbContext"/>. Uses <see cref="IDbContextFactory{TContext}"/>
/// for short-lived contexts so the store can be a singleton without leaking a
/// shared DbContext across threads.
/// </summary>
public sealed class EfCorePromptUsageStore : IPromptUsageStore
{
    private readonly IDbContextFactory<PromptUsageDbContext> _contextFactory;

    /// <summary>Initializes a new instance.</summary>
    public EfCorePromptUsageStore(IDbContextFactory<PromptUsageDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
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
