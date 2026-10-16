using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.SkillTraining;
using Domain.AI.SkillTraining;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.SkillTraining;

/// <summary>
/// SQLite-backed <see cref="ISkillTrainingCheckpointStore"/> (#853): a training run's checkpoints survive a
/// restart, and a claim on a run holds across every process that shares the database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Checkpoints.</b> Each is stored whole, as JSON, under (run, step). Nothing is evicted: unlike the
/// in-memory store, which caps a run at 64 checkpoints to bound memory, a database can hold a run's full
/// history, and resume rebuilds the run's step list from it.
/// </para>
/// <para>
/// <b>The claim on a run</b> is a lease row decided by one SQL statement, so two processes starting the
/// same run id at the same moment cannot both win. It lapses after <see cref="LeaseDuration"/> unless the
/// holder keeps renewing it, which it does on a timer while it holds the claim; a process that dies stops
/// renewing, and the run can be resumed once the lease has run out.
/// </para>
/// </remarks>
public sealed class EfSkillTrainingCheckpointStore : ISkillTrainingCheckpointStore
{
    /// <summary>How long a claim on a run lasts without being renewed.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDbContextFactory<SkillTrainingDbContext> _factory;
    private readonly TimeProvider _time;
    private readonly ILogger<EfSkillTrainingCheckpointStore> _logger;

    /// <summary>Initializes the store.</summary>
    /// <param name="factory">Creates the short-lived contexts each call uses.</param>
    /// <param name="schemaInitializer">Demanded so the schema exists before the first call (resolving the store creates it).</param>
    /// <param name="time">The clock leases are measured against.</param>
    /// <param name="logger">Logger.</param>
    public EfSkillTrainingCheckpointStore(
        IDbContextFactory<SkillTrainingDbContext> factory,
        SchemaInitializer<SkillTrainingDbContext> schemaInitializer,
        TimeProvider time,
        ILogger<EfSkillTrainingCheckpointStore> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(schemaInitializer);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SaveAsync(SkillTrainingCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var payload = JsonSerializer.Serialize(checkpoint, Json);
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await ctx.Checkpoints.FindAsync([checkpoint.RunId, checkpoint.Step], cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            ctx.Checkpoints.Add(new SkillTrainingCheckpointEntity
            {
                RunId = checkpoint.RunId,
                Step = checkpoint.Step,
                Score = checkpoint.Score,
                PayloadJson = payload
            });
        }
        else
        {
            existing.Score = checkpoint.Score;
            existing.PayloadJson = payload;
        }

        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SkillTrainingCheckpoint?> GetAsync(string runId, int step, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await ctx.Checkpoints.AsNoTracking()
            .FirstOrDefaultAsync(e => e.RunId == runId && e.Step == step, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : Read(entity);
    }

    /// <inheritdoc />
    public async Task<SkillTrainingCheckpoint?> GetBestAsync(string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await ctx.Checkpoints.AsNoTracking()
            .Where(e => e.RunId == runId)
            .OrderByDescending(e => e.Score).ThenByDescending(e => e.Step)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return entity is null ? null : Read(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SkillTrainingCheckpoint>> ListAsync(string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entities = await ctx.Checkpoints.AsNoTracking()
            .Where(e => e.RunId == runId)
            .OrderBy(e => e.Step)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. entities.Select(Read)];
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryBeginRunAsync(string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        var token = Guid.NewGuid().ToString("N");
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var expires = now + (long)LeaseDuration.TotalMilliseconds;

        // One statement decides the claim: insert the row, or take over a row whose lease has run out.
        // A live lease makes the conflict branch's WHERE false, so no row changes and the claim is refused.
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var changed = await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO SkillTrainingRunLeases (RunId, OwnerToken, ExpiresAtUnixMs)
            VALUES ({runId}, {token}, {expires})
            ON CONFLICT (RunId) DO UPDATE
            SET OwnerToken = excluded.OwnerToken, ExpiresAtUnixMs = excluded.ExpiresAtUnixMs
            WHERE SkillTrainingRunLeases.ExpiresAtUnixMs <= {now}
            """,
            cancellationToken).ConfigureAwait(false);

        return changed == 0 ? null : new SkillTrainingRunLease(_factory, _time, _logger, runId, token, LeaseDuration);
    }

    private static SkillTrainingCheckpoint Read(SkillTrainingCheckpointEntity entity) =>
        JsonSerializer.Deserialize<SkillTrainingCheckpoint>(entity.PayloadJson, Json)
        ?? throw new InvalidOperationException($"Stored checkpoint {entity.RunId}/{entity.Step} is empty.");
}
