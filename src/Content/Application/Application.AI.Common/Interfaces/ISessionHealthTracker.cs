namespace Application.AI.Common.Interfaces;

/// <summary>
/// Tracks per-agent session health and exposes an observable gauge for Prometheus scraping.
/// Health score: 0 = red (erroring), 1 = yellow (degraded), 2 = green (healthy).
/// </summary>
public interface ISessionHealthTracker
{
    /// <summary>Records a successful agent turn for the given agent.</summary>
    void RecordSuccess(string agentName);

    /// <summary>Records a failed agent turn for the given agent.</summary>
    void RecordError(string agentName);
}
