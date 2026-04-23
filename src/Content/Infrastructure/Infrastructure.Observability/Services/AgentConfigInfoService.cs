using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Observability.Services;

/// <summary>
/// Registers an ObservableGauge that reports agent configuration as metric labels.
/// The gauge always returns 1; the useful data is in the label dimensions
/// (model, temperature, tool/skill/MCP counts) which Grafana reads as a table.
/// </summary>
/// <remarks>
/// Call <see cref="RegisterAgent"/> from the agent factory or orchestrator
/// when an agent is created or its configuration changes.
/// </remarks>
public sealed class AgentConfigInfoService : IAgentConfigReporter
{
    private readonly ILogger<AgentConfigInfoService> _logger;
    private readonly ConcurrentDictionary<string, AgentConfigSnapshot> _configs = new();

    public AgentConfigInfoService(ILogger<AgentConfigInfoService> logger)
    {
        _logger = logger;

        AppInstrument.Meter.CreateObservableGauge(
            AgentConventions.ConfigInfo,
            ObserveConfigs,
            "{info}",
            "Agent configuration info (always 1, labels carry config data)");

        _logger.LogInformation("Agent config info service initialized");
    }

    /// <summary>
    /// Registers or updates an agent's configuration snapshot for metric reporting.
    /// </summary>
    public void RegisterAgent(
        string agentName,
        string model,
        string temperature,
        int toolsCount,
        int skillsCount,
        int mcpServersCount)
    {
        _configs[agentName] = new AgentConfigSnapshot(model, temperature, toolsCount, skillsCount, mcpServersCount);
        _logger.LogDebug("Registered config for agent {Agent}: model={Model}, tools={Tools}, skills={Skills}, mcp={Mcp}",
            agentName, model, toolsCount, skillsCount, mcpServersCount);
    }

    /// <summary>
    /// Removes an agent from config reporting (e.g., on disposal).
    /// </summary>
    public void UnregisterAgent(string agentName)
    {
        _configs.TryRemove(agentName, out _);
    }

    private IEnumerable<Measurement<int>> ObserveConfigs()
    {
        foreach (var (agentName, config) in _configs)
        {
            yield return new Measurement<int>(1,
                new KeyValuePair<string, object?>(AgentConventions.Name, agentName),
                new KeyValuePair<string, object?>(AgentConventions.ConfigModel, config.Model),
                new KeyValuePair<string, object?>(AgentConventions.ConfigTemperature, config.Temperature),
                new KeyValuePair<string, object?>(AgentConventions.ConfigToolsCount, config.ToolsCount.ToString()),
                new KeyValuePair<string, object?>(AgentConventions.ConfigSkillsCount, config.SkillsCount.ToString()),
                new KeyValuePair<string, object?>(AgentConventions.ConfigMcpServersCount, config.McpServersCount.ToString()));
        }
    }

    private sealed record AgentConfigSnapshot(
        string Model,
        string Temperature,
        int ToolsCount,
        int SkillsCount,
        int McpServersCount);
}
