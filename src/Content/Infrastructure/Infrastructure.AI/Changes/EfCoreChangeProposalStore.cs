using System.Text.Json;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Changes;
using Domain.Common.Config;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Changes;

/// <summary>
/// SQLite-backed <see cref="IChangeProposalStore"/> over
/// <see cref="GovernanceStateDbContext"/>, so proposals survive host restarts. Resolved as the
/// active store when <c>AppConfig:AI:Governance:DurableState:ChangeProposalsEnabled</c> is
/// true; otherwise <see cref="InMemoryChangeProposalStore"/> remains active and this type is
/// never constructed.
/// </summary>
/// <remarks>
/// <para>
/// Honors the <see cref="IChangeProposalStore"/> contract exactly as the in-memory store does:
/// <see cref="SaveAsync"/> is an idempotent last-write-wins upsert on the deterministic
/// proposal id, and <see cref="ListAsync"/> AND-combines the query's filters, orders
/// most-recently-submitted first, and caps at <c>MaxResults</c> (a non-positive cap yields an
/// empty list). Filterable dimensions are denormalized into indexed columns so lists translate
/// to SQL; the aggregate itself round-trips as JSON through
/// <see cref="GovernanceStateJson"/> (including the polymorphic target via
/// <see cref="ChangeTargetJsonConverter"/>).
/// </para>
/// <para>
/// Rows whose JSON no longer deserializes are skipped with an error log rather than failing
/// the read — a poisoned row must not take down every list; it stays in the table for manual
/// inspection.
/// </para>
/// </remarks>
public sealed class EfCoreChangeProposalStore : IChangeProposalStore
{
    private readonly IDbContextFactory<GovernanceStateDbContext> _contextFactory;
    private readonly IGovernanceRecordSealer _sealer;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<EfCoreChangeProposalStore> _logger;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="contextFactory">Factory for short-lived governance-state contexts.</param>
    /// <param name="schemaInitializer">
    /// Forces schema creation and evolution before the first operation. Unused beyond its
    /// construction side effect — mirrors <c>EfCorePlanStateStore</c>.
    /// </param>
    /// <param name="sealer">Produces and verifies the tamper-evident seal over stored proposals.</param>
    /// <param name="config">Supplies the payload-size cap.</param>
    /// <param name="logger">Structured logger.</param>
    public EfCoreChangeProposalStore(
        IDbContextFactory<GovernanceStateDbContext> contextFactory,
        GovernanceStateSchemaInitializer schemaInitializer,
        IGovernanceRecordSealer sealer,
        IOptionsMonitor<AppConfig> config,
        ILogger<EfCoreChangeProposalStore> logger)
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

    /// <inheritdoc />
    public async Task<ChangeProposal?> GetAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await context.ChangeProposals
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.ProposalJson, p.ProposalSealJson })
            .FirstOrDefaultAsync(cancellationToken);

        return row?.ProposalJson is null
            ? null
            : await TryLoadAsync(id, row.ProposalJson, row.ProposalSealJson, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveAsync(ChangeProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        // Serialize (and size-check) before opening a context, so an oversized aggregate is
        // rejected without ever reaching SaveChangesAsync.
        var proposalJson = SerializeGuarded(proposal);

        // Seal the exact bytes about to be stored, bound to the proposal id. Without this a
        // database writer could flip Status to an approved or terminal value, or widen the
        // target's scope, and every later read would serve it unchallenged.
        var seal = await _sealer.SealAsync(proposal.Id, proposalJson, cancellationToken);
        var sealJson = JsonSerializer.Serialize(seal, GovernanceStateJson.Options);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await context.ChangeProposals.FindAsync([proposal.Id], cancellationToken);
        if (entity is null)
        {
            entity = new ChangeProposalEntity { Id = proposal.Id };
            context.ChangeProposals.Add(entity);
        }

        entity.Status = proposal.Status.ToString();
        entity.SubmittedByAgentId = proposal.SubmittedBy.Id;
        entity.BlastRadius = (int)proposal.BlastRadius;
        entity.TargetKind = proposal.Target.Kind.ToString();
        entity.SubmittedAtTicks = proposal.SubmittedAt.UtcTicks;
        entity.ProposalJson = proposalJson;
        entity.ProposalSealJson = sealJson;
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChangeProposal>> ListAsync(
        ChangeProposalQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = context.ChangeProposals.AsNoTracking();

        if (query.Status.HasValue)
        {
            var status = query.Status.Value.ToString();
            rows = rows.Where(p => p.Status == status);
        }

        if (!string.IsNullOrEmpty(query.SubmittedByAgentId))
            rows = rows.Where(p => p.SubmittedByAgentId == query.SubmittedByAgentId);

        if (query.MinimumBlastRadius.HasValue)
        {
            var minimum = (int)query.MinimumBlastRadius.Value;
            rows = rows.Where(p => p.BlastRadius >= minimum);
        }

        if (query.TargetKind.HasValue)
        {
            var kind = query.TargetKind.Value.ToString();
            rows = rows.Where(p => p.TargetKind == kind);
        }

        var payloads = await rows
            .OrderByDescending(p => p.SubmittedAtTicks)
            .Take(Math.Max(0, query.MaxResults))
            .Select(p => new { p.Id, p.ProposalJson, p.ProposalSealJson })
            .ToListAsync(cancellationToken);

        var results = new List<ChangeProposal>(payloads.Count);
        foreach (var payload in payloads)
        {
            if (payload.Id is null || payload.ProposalJson is null)
                continue;

            var proposal = await TryLoadAsync(
                payload.Id, payload.ProposalJson, payload.ProposalSealJson, cancellationToken);
            if (proposal is not null)
                results.Add(proposal);
        }

        return results;
    }

    /// <summary>
    /// Deserializes a stored proposal and clears it for use: the payload must parse, the id
    /// inside it must match the row it was loaded from, and its seal must verify against both.
    /// Any failure quarantines that one row (logged, skipped, preserved on disk).
    /// </summary>
    /// <param name="id">The row's primary key.</param>
    /// <param name="proposalJson">The stored aggregate JSON.</param>
    /// <param name="sealJson">The stored seal JSON, or null on a row written before sealing.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<ChangeProposal?> TryLoadAsync(
        string id, string proposalJson, string? sealJson, CancellationToken ct)
    {
        var proposal = GovernanceStatePayload.TryDeserialize<ChangeProposal>(
            proposalJson, _logger, "change proposal", id);
        if (proposal is null)
            return null;

        // Identity gate, independent of the seal: a payload relocated between rows is rejected
        // before its (id-bound) seal is even consulted.
        if (!string.Equals(proposal.Id, id, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Stored change proposal under key {RowId} declares id {DeclaredId}; refusing to serve it",
                id, proposal.Id);
            return null;
        }

        var seal = sealJson is null
            ? null
            : GovernanceStatePayload.TryDeserialize<GovernanceRecordSeal>(
                sealJson, _logger, "change proposal seal", id);

        if (await _sealer.VerifyAsync(id, proposalJson, seal, ct))
            return proposal;

        _logger.LogError(
            "Change proposal {ProposalId} failed seal verification; refusing to serve it " +
            "(the row is preserved for investigation)",
            id);
        return null;
    }

    /// <summary>
    /// Serializes the aggregate and rejects it when it exceeds the configured cap, so an
    /// oversized proposal fails loudly at the write rather than being stored and failing every
    /// later read.
    /// </summary>
    /// <param name="proposal">The proposal to serialize.</param>
    private string SerializeGuarded(ChangeProposal proposal) =>
        GovernanceStatePayload.SerializeGuarded(
            proposal,
            _config.CurrentValue.AI.Governance.DurableState.MaxPayloadBytes,
            $"change proposal {proposal.Id}");
}
