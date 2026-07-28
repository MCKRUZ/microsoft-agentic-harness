using System.Text.Json;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.Common.Config;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Escalation;

/// <summary>
/// SQLite-backed <see cref="IEscalationStateStore"/> over
/// <see cref="GovernanceStateDbContext"/>. Registered as the active store when
/// <c>AppConfig:AI:Governance:DurableState:EscalationsEnabled</c> is true.
/// </summary>
/// <remarks>
/// <para>
/// Uses short-lived contexts from <see cref="IDbContextFactory{TContext}"/> per operation, the
/// repo's convention for singleton stores. Demands the schema initializer as a plain
/// constructor dependency (visible to ValidateOnBuild) so the schema exists before the first
/// write; because this store is only constructed when durable escalation state is enabled,
/// hosts running with the default-off configuration never create the database file. Write
/// failures propagate as exceptions — the escalation service's fail-closed contract depends on
/// that, and the service wraps them in a scrubbed <c>EscalationDurableStateException</c>
/// before they can reach a transport.
/// </para>
/// <para>
/// <b>Reads treat the database file as untrusted input.</b> Rows are projected as raw
/// primitives and every conversion — tick-to-<see cref="DateTimeOffset"/>, status parse, JSON
/// deserialization — happens inside a guarded per-row mapping. Nothing that can throw runs
/// during query materialization, so one corrupt column quarantines its own row and leaves
/// startup rehydration free to recover every other escalation.
/// </para>
/// <para>
/// Resolved outcomes are sealed via <see cref="IEscalationOutcomeSealer"/> on write and
/// verified on read; an outcome whose seal does not verify is withheld, so the reconciler
/// never re-drives a possibly forged verdict into the compliance audit log.
/// </para>
/// </remarks>
public sealed class EfCoreEscalationStateStore : IEscalationStateStore
{
    private readonly IDbContextFactory<GovernanceStateDbContext> _contextFactory;
    private readonly IGovernanceRecordSealer _sealer;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<EfCoreEscalationStateStore> _logger;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="contextFactory">Factory for short-lived governance-state contexts.</param>
    /// <param name="schemaInitializer">
    /// Forces schema creation and evolution before the first operation. Unused beyond its
    /// construction side effect — mirrors <c>EfCorePlanStateStore</c>.
    /// </param>
    /// <param name="sealer">Produces and verifies the tamper-evident seal over stored outcomes.</param>
    /// <param name="config">Supplies the scan bound and payload-size cap.</param>
    /// <param name="logger">Structured logger.</param>
    public EfCoreEscalationStateStore(
        IDbContextFactory<GovernanceStateDbContext> contextFactory,
        GovernanceStateSchemaInitializer schemaInitializer,
        IGovernanceRecordSealer sealer,
        IOptionsMonitor<AppConfig> config,
        ILogger<EfCoreEscalationStateStore> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(schemaInitializer);
        ArgumentNullException.ThrowIfNull(sealer);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _sealer = sealer;
        _config = config;
        _logger = logger;
    }

    private int MaxScanRecords =>
        Math.Max(1, _config.CurrentValue.AI.Governance.DurableState.MaxScanRecords);

    private int MaxPayloadBytes =>
        Math.Max(1024, _config.CurrentValue.AI.Governance.DurableState.MaxPayloadBytes);

    /// <inheritdoc />
    public async Task SavePendingAsync(EscalationRequest request, DateTimeOffset createdAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestJson = SerializeGuarded(request, nameof(EscalationStateEntity.RequestJson));

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await context.Escalations.FindAsync([request.EscalationId], ct);
        if (entity is null)
        {
            entity = new EscalationStateEntity
            {
                Id = request.EscalationId,
                CreatedAtTicks = createdAt.UtcTicks
            };
            context.Escalations.Add(entity);
        }

        entity.Status = nameof(EscalationPersistedStatus.Pending);
        entity.RequestJson = requestJson;
        entity.DecisionsJson = "[]";
        entity.OutcomeJson = null;
        entity.OutcomeSealJson = null;
        entity.UpdatedAtTicks = DateTimeOffset.UtcNow.UtcTicks;
        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SaveDecisionsAsync(
        Guid escalationId, IReadOnlyList<ApproverDecision> decisions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var decisionsJson = SerializeGuarded(decisions, nameof(EscalationStateEntity.DecisionsJson));

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await context.Escalations.FindAsync([escalationId], ct)
            ?? throw new InvalidOperationException(
                $"No durable state record exists for escalation {escalationId}; refusing to record a decision against it.");

        entity.DecisionsJson = decisionsJson;
        entity.UpdatedAtTicks = DateTimeOffset.UtcNow.UtcTicks;
        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task MarkResolvedPendingAuditAsync(EscalationOutcome outcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var outcomeJson = SerializeGuarded(outcome, nameof(EscalationStateEntity.OutcomeJson));
        var decisionsJson = SerializeGuarded(outcome.Decisions, nameof(EscalationStateEntity.DecisionsJson));

        // Seal the exact bytes about to be stored, bound to this escalation's id, so a seal
        // cannot be lifted onto another row and verification later compares like for like.
        var seal = await _sealer.SealAsync(SubjectId(outcome.EscalationId), outcomeJson, ct);
        var sealJson = SerializeGuarded(seal, nameof(EscalationStateEntity.OutcomeSealJson));

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await context.Escalations.FindAsync([outcome.EscalationId], ct)
            ?? throw new InvalidOperationException(
                $"No durable state record exists for escalation {outcome.EscalationId}; refusing to record its resolution.");

        entity.Status = nameof(EscalationPersistedStatus.ResolvedPendingAudit);
        entity.OutcomeJson = outcomeJson;
        entity.OutcomeSealJson = sealJson;
        entity.DecisionsJson = decisionsJson;
        entity.UpdatedAtTicks = DateTimeOffset.UtcNow.UtcTicks;
        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task MarkResolvedAsync(Guid escalationId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await context.Escalations.FindAsync([escalationId], ct)
            ?? throw new InvalidOperationException(
                $"No durable state record exists for escalation {escalationId}; cannot mark it resolved.");

        entity.Status = nameof(EscalationPersistedStatus.Resolved);
        entity.UpdatedAtTicks = DateTimeOffset.UtcNow.UtcTicks;
        await context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimResolvedPendingAuditAsync(
        Guid escalationId, DateTimeOffset staleClaimBefore, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        var staleBeforeTicks = staleClaimBefore.UtcTicks;

        // Conditional claim: only the caller whose UPDATE actually matched owns the re-drive.
        // A concurrent second pass matches zero rows and backs off, so the compliance audit
        // line and the resolution notification fire once per stuck escalation.
        //
        // The second disjunct reclaims ABANDONED claims. A pass that is killed between
        // claiming and finishing (kill -9, OOM, pod eviction) never reaches ReleaseClaimAsync,
        // so without this the row would sit in AuditInFlight forever: no claim would ever
        // match it again, the reconciler would skip it on every pass, and the pruner correctly
        // refuses to delete a non-terminal row. The staleness bound is what keeps this from
        // stealing a claim out from under a pass that is merely slow.
        var claimed = await context.Escalations
            .Where(e => e.Id == escalationId
                && (e.Status == nameof(EscalationPersistedStatus.ResolvedPendingAudit)
                    || (e.Status == nameof(EscalationPersistedStatus.AuditInFlight)
                        && e.UpdatedAtTicks < staleBeforeTicks)))
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.Status, nameof(EscalationPersistedStatus.AuditInFlight))
                      .SetProperty(e => e.UpdatedAtTicks, DateTimeOffset.UtcNow.UtcTicks),
                ct);

        return claimed > 0;
    }

    /// <inheritdoc />
    public async Task ReleaseClaimAsync(Guid escalationId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        await context.Escalations
            .Where(e => e.Id == escalationId
                && e.Status == nameof(EscalationPersistedStatus.AuditInFlight))
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.Status, nameof(EscalationPersistedStatus.ResolvedPendingAudit))
                      .SetProperty(e => e.UpdatedAtTicks, DateTimeOffset.UtcNow.UtcTicks),
                ct);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Guid escalationId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        await context.Escalations
            .Where(e => e.Id == escalationId)
            .ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EscalationStateSnapshot>> GetActiveAsync(CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        // Status filter pushed into SQL (backed by ix_escalation_state_status_updated_at) and
        // the scan bounded, so a pathological backlog cannot blow out startup time or memory.
        // Raw, NULLABLE primitives only — the payload and status columns are as untrusted as
        // the ticks were, so a NULL in any of them must surface as a null string inside the
        // guarded mapping rather than as a materialization throw outside it.
        var rows = await context.Escalations
            .AsNoTracking()
            .Where(e => e.Status != nameof(EscalationPersistedStatus.Resolved))
            .OrderBy(e => e.UpdatedAtTicks)
            .Take(MaxScanRecords)
            .Select(e => new RawEscalationRow(
                e.Id, e.Status, e.RequestJson, e.DecisionsJson,
                e.OutcomeJson, e.OutcomeSealJson, e.CreatedAtTicks))
            .ToListAsync(ct);

        var snapshots = new List<EscalationStateSnapshot>(rows.Count);
        foreach (var row in rows)
        {
            var snapshot = TryMapSnapshot(row);
            if (snapshot is not null)
                snapshots.Add(await ApplySealVerificationAsync(snapshot, row, ct));
        }

        return snapshots;
    }

    /// <inheritdoc />
    public async Task<EscalationOutcome?> GetResolvedOutcomeAsync(Guid escalationId, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        var row = await context.Escalations
            .AsNoTracking()
            .Where(e => e.Id == escalationId && e.Status == nameof(EscalationPersistedStatus.Resolved))
            .Select(e => new { e.OutcomeJson, e.OutcomeSealJson })
            .FirstOrDefaultAsync(ct);

        if (row?.OutcomeJson is null)
            return null;

        var outcome = TryDeserialize<EscalationOutcome>(row.OutcomeJson, escalationId, "outcome");
        if (outcome is null)
            return null;

        // Identity gate. Without it, an approved outcome copied verbatim into another row
        // would verify byte-for-byte and this method would return escalation A's approval when
        // asked about escalation B — and PlanExecutor.Recovery branches a human gate on
        // IsApproved without re-checking the id, so one real approval would approve everything
        // after it.
        if (outcome.EscalationId != escalationId)
        {
            _logger.LogError(
                "Stored outcome for escalation {EscalationId} declares {DeclaredId}; refusing to serve it",
                escalationId, outcome.EscalationId);
            return null;
        }

        // A terminal outcome is already in the compliance log, but it still drives caller
        // decisions (the plan executor resumes a blocked step on it), so a tampered row must
        // not be served as a verdict.
        var seal = row.OutcomeSealJson is null
            ? null
            : TryDeserialize<GovernanceRecordSeal>(row.OutcomeSealJson, escalationId, "outcome seal");

        if (await _sealer.VerifyAsync(SubjectId(escalationId), row.OutcomeJson, seal, ct))
            return outcome;

        _logger.LogError(
            "Persisted outcome for escalation {EscalationId} failed seal verification; refusing to serve it",
            escalationId);
        return null;
    }

    /// <summary>
    /// Verifies the seal on a snapshot carrying an outcome, stripping that outcome when
    /// verification fails so the reconciler skips it instead of re-driving a possibly forged
    /// verdict.
    /// </summary>
    /// <param name="snapshot">The mapped snapshot.</param>
    /// <param name="row">The raw row it came from.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<EscalationStateSnapshot> ApplySealVerificationAsync(
        EscalationStateSnapshot snapshot, RawEscalationRow row, CancellationToken ct)
    {
        if (snapshot.Outcome is null || row.OutcomeJson is null)
            return snapshot;

        // Belt-and-braces identity gate. TryMapSnapshot already rejects a relocated payload,
        // but this is the last checkpoint before the reconciler re-drives a verdict into the
        // hash-chained compliance log, so the check is repeated rather than assumed.
        if (snapshot.Outcome.EscalationId != row.Id)
        {
            _logger.LogError(
                "Durable escalation {EscalationId} holds an outcome declaring {DeclaredId}; " +
                "quarantining it (a verdict relocated between rows is never re-driven)",
                row.Id, snapshot.Outcome.EscalationId);
            return snapshot with { Outcome = null };
        }

        var seal = row.OutcomeSealJson is null
            ? null
            : TryDeserialize<GovernanceRecordSeal>(row.OutcomeSealJson, row.Id, "outcome seal");

        if (await _sealer.VerifyAsync(SubjectId(row.Id), row.OutcomeJson, seal, ct))
            return snapshot;

        _logger.LogError(
            "Durable escalation {EscalationId} carries an outcome whose seal does not verify; " +
            "quarantining it (the row is preserved for investigation and will not be re-driven)",
            row.Id);
        return snapshot with { Outcome = null };
    }

    /// <summary>
    /// Maps one raw row to a snapshot, or returns null (with an error log) when any of its
    /// values are unreadable. Every conversion that can throw lives inside this guard, which
    /// is what keeps a single poisoned row from failing an entire scan — and, through the
    /// startup rehydration path, the host boot.
    /// </summary>
    /// <param name="row">The raw row to map.</param>
    private EscalationStateSnapshot? TryMapSnapshot(RawEscalationRow row)
    {
        try
        {
            // NULLs in NOT NULL columns are possible in a file this process did not write.
            if (row.Status is null || row.RequestJson is null)
                throw new JsonException("Row is missing its status or request payload.");

            var request = JsonSerializer.Deserialize<EscalationRequest>(row.RequestJson, GovernanceStateJson.Options)
                ?? throw new JsonException("Request payload deserialized to null.");
            var decisions = row.DecisionsJson is null
                ? []
                : JsonSerializer.Deserialize<List<ApproverDecision>>(row.DecisionsJson, GovernanceStateJson.Options) ?? [];
            var outcome = row.OutcomeJson is null
                ? null
                : JsonSerializer.Deserialize<EscalationOutcome>(row.OutcomeJson, GovernanceStateJson.Options);

            // Identity gate, independent of the seal: the escalation id INSIDE a payload must
            // match the row it was loaded from. A payload relocated between rows is rejected
            // here even before its (id-bound) seal is checked.
            if (request.EscalationId != row.Id)
            {
                throw new JsonException(
                    $"Request payload declares escalation {request.EscalationId} but is stored under {row.Id}.");
            }

            // A relocated OUTCOME strips just the verdict rather than dropping the whole row:
            // the request may still be legitimate, and an escalation that vanishes entirely is
            // invisible to operators. A null outcome is already the signal the reconciler uses
            // to skip a record, so the dangerous half is neutralized either way.
            if (outcome is not null && outcome.EscalationId != row.Id)
            {
                _logger.LogError(
                    "Durable escalation {EscalationId} holds an outcome declaring {DeclaredId}; " +
                    "withholding the verdict (a relocated approval is never honoured)",
                    row.Id, outcome.EscalationId);
                outcome = null;
            }

            return new EscalationStateSnapshot
            {
                Request = request,
                Decisions = decisions,
                CreatedAt = new DateTimeOffset(row.CreatedAtTicks, TimeSpan.Zero),
                Status = Enum.Parse<EscalationPersistedStatus>(row.Status, ignoreCase: true),
                Outcome = outcome
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or OverflowException)
        {
            _logger.LogError(ex,
                "Skipping unreadable durable escalation record {EscalationId} (status {Status}); " +
                "the row is preserved for manual inspection",
                row.Id, row.Status);
            return null;
        }
    }

    /// <summary>Deserializes a payload, logging and returning null when it is unreadable.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="json">The stored JSON.</param>
    /// <param name="escalationId">The owning escalation, for the log line.</param>
    /// <param name="description">What the payload is, for the log line.</param>
    private T? TryDeserialize<T>(string json, Guid escalationId, string description)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, GovernanceStateJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or OverflowException)
        {
            _logger.LogError(ex,
                "Failed to deserialize the persisted {Description} for escalation {EscalationId}",
                description, escalationId);
            return null;
        }
    }

    /// <summary>
    /// Serializes a payload and rejects it when it exceeds the configured cap, so an oversized
    /// document fails loudly at the write rather than being stored and failing every later read.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="columnName">The destination column, for the failure message.</param>
    private string SerializeGuarded<T>(T value, string columnName)
    {
        var json = JsonSerializer.Serialize(value, GovernanceStateJson.Options);
        var maxBytes = MaxPayloadBytes;
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(json);

        if (byteCount > maxBytes)
        {
            throw new InvalidOperationException(
                $"Governance-state payload for {columnName} is {byteCount} bytes, exceeding the configured " +
                $"maximum of {maxBytes}. Raise AppConfig:AI:Governance:DurableState:MaxPayloadBytes or " +
                "reduce the payload size.");
        }

        return json;
    }

    /// <summary>
    /// Raw column values projected from SQLite, before any throwing conversion.
    /// </summary>
    /// <remarks>
    /// Every string is nullable even where the schema declares NOT NULL: the schema constrains
    /// what this process writes, not what is in the file. The one column that cannot be
    /// deferred this way is <see cref="Id"/> — EF converts the Guid BLOB during materialization,
    /// so a truncated blob throws inside the query rather than in the per-row guard. That
    /// residual case is contained one level up, where the rehydration host service treats a
    /// scan-level failure as non-fatal (LogCritical and continue) instead of failing host boot.
    /// </remarks>
    private sealed record RawEscalationRow(
        Guid Id,
        string? Status,
        string? RequestJson,
        string? DecisionsJson,
        string? OutcomeJson,
        string? OutcomeSealJson,
        long CreatedAtTicks);

    /// <summary>
    /// The seal subject for an escalation: its id in a stable, delimiter-free form. Binding
    /// this into the MAC is what makes a seal valid for exactly one row.
    /// </summary>
    /// <param name="escalationId">The escalation the seal belongs to.</param>
    private static string SubjectId(Guid escalationId) => escalationId.ToString("N");
}
