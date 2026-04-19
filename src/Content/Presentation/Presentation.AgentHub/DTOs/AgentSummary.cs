namespace Presentation.AgentHub.DTOs;

/// <summary>Lightweight DTO returned by GET /api/agents.</summary>
/// <param name="Id">Stable identifier used to address the agent in subsequent SignalR calls.</param>
/// <param name="Name">Human-readable display name shown in the UI.</param>
/// <param name="Description">Short description of the agent's purpose.</param>
public sealed record AgentSummary(string Id, string Name, string Description);
