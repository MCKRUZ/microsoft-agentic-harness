using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Resilience;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Resilience;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Resilience;

/// <summary>
/// Bridges Polly v8 circuit breaker state to the domain <see cref="ProviderHealthState"/> enum.
/// Exposes per-provider health through <see cref="IProviderHealthMonitor"/> and fires
/// state-change callbacks for OTel gauge updates and retry queue drain triggers.
/// </summary>
/// <remarks>
/// <para>
/// Does not perform synthetic pre-warm probes. Recovery detection relies on Polly's
/// built-in half-open behavior: when a circuit transitions to HalfOpen, the next real
/// request serves as the recovery probe.
/// </para>
/// <para>
/// State transitions are reported by Polly callbacks (OnOpened, OnClosed, OnHalfOpen)
/// registered in <see cref="ProviderResiliencePipelineBuilder"/> during pipeline construction.
/// </para>
/// </remarks>
public sealed class PollyProviderHealthMonitor : IProviderHealthMonitor
{
    private readonly ConcurrentDictionary<string, ProviderHealthState> _providerStates = new();
    private readonly ILogger<PollyProviderHealthMonitor>? _logger;

    /// <summary>Creates a new health monitor instance.</summary>
    /// <param name="logger">Optional logger for state transition logging.</param>
    public PollyProviderHealthMonitor(ILogger<PollyProviderHealthMonitor>? logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public event Action<string, ProviderHealthState>? OnCircuitStateChanged;

    /// <inheritdoc/>
    public ProviderHealthState GetProviderHealth(string providerName)
    {
        return _providerStates.TryGetValue(providerName, out var state) ? state : ProviderHealthState.Healthy;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, ProviderHealthState> GetAllProviderHealth()
    {
        return new Dictionary<string, ProviderHealthState>(_providerStates);
    }

    /// <inheritdoc/>
    public bool IsAnyProviderHealthy()
    {
        if (_providerStates.IsEmpty)
            return true;

        return _providerStates.Values.Any(s => s == ProviderHealthState.Healthy);
    }

    /// <summary>
    /// Called by Polly pipeline callbacks (OnOpened, OnClosed, OnHalfOpen) to report
    /// circuit breaker state transitions. Fires <see cref="OnCircuitStateChanged"/>
    /// only when the state actually changes. Thread-safe for concurrent calls.
    /// </summary>
    /// <param name="providerName">The provider whose circuit state changed.</param>
    /// <param name="newState">The new <see cref="ProviderHealthState"/>.</param>
    public void ReportStateChange(string providerName, ProviderHealthState newState)
    {
        ProviderHealthState? capturedOldState = null;

        _providerStates.AddOrUpdate(
            providerName,
            addValueFactory: _ =>
            {
                var defaultState = ProviderHealthState.Healthy;
                if (defaultState != newState)
                    capturedOldState = defaultState;
                return newState;
            },
            updateValueFactory: (_, existing) =>
            {
                if (existing != newState)
                    capturedOldState = existing;
                return newState;
            });

        if (capturedOldState is null)
            return;

        var oldState = capturedOldState.Value;

        _logger?.LogInformation("Provider {Provider} circuit state changed: {OldState} -> {NewState}",
            providerName, oldState, newState);

        ResilienceMetrics.CircuitStateChanges.Add(1,
            new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, providerName),
            new KeyValuePair<string, object?>(ResilienceConventions.TransitionFrom, oldState.ToString()),
            new KeyValuePair<string, object?>(ResilienceConventions.TransitionTo, newState.ToString()));

        ResilienceMetrics.CircuitState.Add(
            (long)newState - (long)oldState,
            new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, providerName));

        var handler = OnCircuitStateChanged;
        handler?.Invoke(providerName, newState);
    }
}
