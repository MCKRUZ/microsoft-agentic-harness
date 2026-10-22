namespace Infrastructure.AI.BackgroundServices;

/// <summary>
/// Runs a retention sweep's delete in batches (#901): another batch while the last came back full, up to a limit, so a
/// backlog larger than one batch is cleared within one sweep instead of growing between sweeps.
/// </summary>
internal static class SweepBatches
{
    /// <summary>The most batches one sweep runs; the rest wait for the next sweep.</summary>
    internal const int MaxBatches = 20;

    /// <summary>
    /// Calls <paramref name="deleteBatch"/> until it removes fewer than <paramref name="batchSize"/>, or
    /// <see cref="MaxBatches"/> times.
    /// </summary>
    /// <returns>How many records were removed in all.</returns>
    public static async Task<int> RunAsync(Func<Task<int>> deleteBatch, int batchSize)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            var removed = await deleteBatch();
            total += removed;
            if (removed < batchSize)
                break;
        }

        return total;
    }
}
