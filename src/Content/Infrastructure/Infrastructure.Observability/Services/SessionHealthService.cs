using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Observability.Services;

/// <summary>
/// Tracks agent session health via a sliding window of recent turn outcomes
/// and exposes an ObservableGauge for Prometheus scraping.
/// Score: 2 = green (error rate &lt; 10%), 1 = yellow (10-50%), 0 = red (&gt; 50%).
/// </summary>
public sealed class SessionHealthService : ISessionHealthTracker
{
    private readonly ILogger<SessionHealthService> _logger;
    private readonly ConcurrentDictionary<string, AgentHealthState> _states = new();

    public SessionHealthService(ILogger<SessionHealthService> logger)
    {
        _logger = logger;

        AppInstrument.Meter.CreateObservableGauge(
            SessionConventions.HealthScore,
            ObserveHealthScores,
            "{score}",
            "Session health score per agent (0=red, 1=yellow, 2=green)");

        _logger.LogInformation("Session health service initialized with observable gauge");
    }

    public void RecordSuccess(string agentName)
    {
        var state = _states.GetOrAdd(agentName, _ => new AgentHealthState());
        state.RecordOutcome(true);
    }

    public void RecordError(string agentName)
    {
        var state = _states.GetOrAdd(agentName, _ => new AgentHealthState());
        state.RecordOutcome(false);
    }

    private IEnumerable<Measurement<int>> ObserveHealthScores()
    {
        foreach (var (agentName, state) in _states)
        {
            yield return new Measurement<int>(
                state.ComputeScore(),
                new KeyValuePair<string, object?>(AgentConventions.Name, agentName));
        }
    }

    private sealed class AgentHealthState
    {
        private const int WindowSize = 20;
        private readonly Queue<bool> _window = new(WindowSize);
        private readonly object _lock = new();

        public void RecordOutcome(bool success)
        {
            lock (_lock)
            {
                if (_window.Count >= WindowSize)
                    _window.Dequeue();
                _window.Enqueue(success);
            }
        }

        public int ComputeScore()
        {
            lock (_lock)
            {
                if (_window.Count == 0)
                    return 2;

                var errorRate = (double)_window.Count(ok => !ok) / _window.Count;
                return errorRate switch
                {
                    > 0.5 => 0,
                    > 0.1 => 1,
                    _ => 2,
                };
            }
        }
    }
}
