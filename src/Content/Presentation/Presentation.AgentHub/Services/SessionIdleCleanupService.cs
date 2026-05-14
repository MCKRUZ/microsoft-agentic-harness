using System.Diagnostics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.Hubs;

namespace Presentation.AgentHub.Services;

/// <summary>
/// Periodically checks for sessions that have been idle longer than the configured timeout
/// and ends them. Prevents sessions from staying "active" indefinitely when the user stops
/// interacting but keeps the browser tab open.
/// </summary>
internal sealed class SessionIdleCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentHubConfig> _config;
    private readonly ILogger<SessionIdleCleanupService> _logger;

    public SessionIdleCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentHubConfig> config,
        ILogger<SessionIdleCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                await CleanupIdleSessionsAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — expected.
        }
    }

    private async Task CleanupIdleSessionsAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(_config.CurrentValue.SessionIdleTimeoutMinutes);
        var now = DateTimeOffset.UtcNow;
        var expired = new List<(string ConnectionId, AgentTelemetryHub.ActiveConversationInfo Info)>();

        foreach (var (connectionId, info) in AgentTelemetryHub.ConnectionConversations)
        {
            if (now - info.LastActivityAt > timeout)
                expired.Add((connectionId, info));
        }

        if (expired.Count == 0) return;

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IObservabilityStore>();

        foreach (var (connectionId, info) in expired)
        {
            if (!AgentTelemetryHub.ConnectionConversations.TryRemove(connectionId, out _))
                continue;

            SessionMetrics.ActiveSessions.Add(-1,
                new TagList { { AgentConventions.Name, info.AgentName } });

            if (info.TurnCount > 0)
            {
                var elapsed = now - info.StartedAt;
                var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, info.AgentName);
                OrchestrationMetrics.ConversationDuration.Record(elapsed.TotalMilliseconds, agentTag);
                OrchestrationMetrics.TurnsPerConversation.Record(info.TurnCount, agentTag);
            }

            await store.EndSessionAsync(info.ObservabilitySessionId, "completed", cancellationToken: ct);

            _logger.LogInformation(
                "Idle timeout: ended session {SessionId} for agent {AgentName} after {IdleMinutes}m idle.",
                info.ObservabilitySessionId, info.AgentName, _config.CurrentValue.SessionIdleTimeoutMinutes);
        }
    }
}
