using Infrastructure.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.SkillTraining;

/// <summary>
/// A held claim on one training run (#853): renews itself on a timer while held, and gives the claim back
/// when disposed.
/// </summary>
/// <remarks>
/// The timer is why a long run keeps its claim and a crashed one does not: a live process renews, a dead
/// one cannot. If a renewal finds the claim already taken (the lease lapsed during a long stall and another
/// process took over) it logs an error. It cannot stop the run from here, so the log is the signal.
/// </remarks>
internal sealed class SkillTrainingRunLease : IAsyncDisposable
{
    private readonly IDbContextFactory<SkillTrainingDbContext> _factory;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _runId;
    private readonly string _token;
    private readonly TimeSpan _duration;
    private readonly ITimer _timer;
    private int _renewing;

    public SkillTrainingRunLease(
        IDbContextFactory<SkillTrainingDbContext> factory,
        TimeProvider time,
        ILogger logger,
        string runId,
        string token,
        TimeSpan duration)
    {
        _factory = factory;
        _time = time;
        _logger = logger;
        _runId = runId;
        _token = token;
        _duration = duration;

        var interval = duration / 3;
        _timer = time.CreateTimer(_ => _ = RenewAsync(), state: null, dueTime: interval, period: interval);
    }

    private async Task RenewAsync()
    {
        // A slow renewal must not stack another behind it.
        if (Interlocked.Exchange(ref _renewing, 1) == 1)
            return;

        try
        {
            var expires = _time.GetUtcNow().ToUnixTimeMilliseconds() + (long)_duration.TotalMilliseconds;
            await using var ctx = await _factory.CreateDbContextAsync().ConfigureAwait(false);
            var changed = await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE SkillTrainingRunLeases SET ExpiresAtUnixMs = {expires} WHERE RunId = {_runId} AND OwnerToken = {_token}")
                .ConfigureAwait(false);

            if (changed == 0)
            {
                _logger.LogError(
                    "The claim on skill-training run {RunId} was lost; another process may now be working on it.", _runId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not renew the claim on skill-training run {RunId}; it will lapse if this persists.", _runId);
        }
        finally
        {
            Interlocked.Exchange(ref _renewing, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _timer.DisposeAsync().ConfigureAwait(false);

        try
        {
            await using var ctx = await _factory.CreateDbContextAsync().ConfigureAwait(false);
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM SkillTrainingRunLeases WHERE RunId = {_runId} AND OwnerToken = {_token}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Not releasing is safe: the lease lapses on its own. Failing the run over it would not be.
            _logger.LogWarning(ex, "Could not release the claim on skill-training run {RunId}; it will lapse.", _runId);
        }
    }
}
