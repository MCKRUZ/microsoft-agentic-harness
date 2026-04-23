namespace Application.AI.Common.Interfaces;

/// <summary>
/// Reports agent configuration snapshots for observability gauges.
/// Implemented by Infrastructure.Observability to bridge the ObservableGauge
/// registration without creating a layer dependency from Application → Infrastructure.
/// </summary>
public interface IAgentConfigReporter
{
    /// <summary>
    /// Registers or updates an agent's configuration snapshot for metric reporting.
    /// </summary>
    void RegisterAgent(
        string agentName,
        string model,
        string temperature,
        int toolsCount,
        int skillsCount,
        int mcpServersCount);

    /// <summary>
    /// Removes an agent from config reporting.
    /// </summary>
    void UnregisterAgent(string agentName);
}
