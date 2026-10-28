using Application.AI.Common.Interfaces.Runs;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Tells every holder of run-scoped state that some runs' records are gone. Shared by the retention sweep and by erasure
/// (#926), which both remove run records and must release what else is keyed by the same ids.
/// </summary>
internal static class RunReclaimNotifier
{
    /// <summary>Calls every listener with <paramref name="jobIds"/>, guarding each one separately.</summary>
    /// <remarks>
    /// One that throws must not stop the rest from being told: the records are already gone by this point, so a listener
    /// skipped here holds its entries for the life of the process with nothing that will ever come back for them, which
    /// is the unbounded leak this call exists to prevent, reintroduced by a fault in an unrelated holder.
    /// </remarks>
    /// <param name="listeners">Every holder of run-scoped state.</param>
    /// <param name="jobIds">The runs whose records were removed; nothing is called when empty.</param>
    /// <param name="logger">Logger for a listener that fails.</param>
    public static void Notify(IReadOnlyList<IRunReclaimListener> listeners, IReadOnlyList<string> jobIds, ILogger logger)
    {
        if (jobIds.Count == 0)
            return;

        foreach (var listener in listeners)
        {
            try
            {
                listener.OnRunsReclaimed(jobIds);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "{Listener} failed to release state for {Count} reclaimed run(s); its entries for "
                    + "those runs will not be recovered.",
                    listener.GetType().Name,
                    jobIds.Count);
            }
        }
    }
}
