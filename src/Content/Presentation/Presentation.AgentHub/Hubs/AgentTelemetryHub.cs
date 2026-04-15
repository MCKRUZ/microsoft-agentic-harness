using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Presentation.AgentHub.Hubs;

/// <summary>
/// SignalR hub that streams agent telemetry to connected clients.
/// Stub implementation — full wiring added in section 04.
/// </summary>
[Authorize]
public class AgentTelemetryHub : Hub
{
}
