namespace Presentation.AgentHub.Models;

/// <summary>Lightweight DTO returned by GET /api/agents.</summary>
public sealed record AgentSummary(string Name, string Description);
