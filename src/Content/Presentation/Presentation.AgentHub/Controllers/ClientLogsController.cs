using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Presentation.AgentHub.DTOs;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// Ingests browser log entries and replays them into the server's <see cref="ILogger"/>
/// pipeline so the Serilog file sink captures frontend and backend events in one
/// ndjson stream — enabling cross-tier grep/correlation from a single source.
/// </summary>
/// <remarks>
/// Requires auth: browsers can only post after authentication completes (DevAuth in dev,
/// MSAL bearer in prod). Batches are capped at <see cref="MaxBatchSize"/>; exceeding
/// the cap yields 413 to prevent a chatty client from flooding the log pipeline.
/// </remarks>
[ApiController]
[Route("api/client-logs")]
[Authorize]
public sealed class ClientLogsController : ControllerBase
{
    private const int MaxBatchSize = 100;
    private const int MaxMessageLength = 8_000;

    private readonly ILogger<ClientLogsController> _logger;

    /// <summary>Initialises the controller with its logger dependency.</summary>
    public ClientLogsController(ILogger<ClientLogsController> logger) => _logger = logger;

    /// <summary>Accepts a batch of browser log entries and projects them onto the server logger.</summary>
    [HttpPost]
    public IActionResult Post([FromBody] IReadOnlyList<ClientLogEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
            return BadRequest();
        if (entries.Count > MaxBatchSize)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);

        foreach (var entry in entries)
        {
            var level = ParseLevel(entry.Level);
            var message = Truncate(entry.Message, MaxMessageLength);

            // Scope fields propagate to every ILogger call until the using-block exits,
            // letting Serilog emit them as first-class structured properties in the ndjson.
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["ClientOrigin"] = "webui",
                ["ClientSessionId"] = entry.SessionId,
                ["ClientTimestamp"] = entry.Timestamp,
                ["ClientUrl"] = entry.Url,
                ["ClientStack"] = entry.Stack,
            });

            _logger.Log(level, "[client] {ClientMessage}", message);
        }

        return Accepted();
    }

    private static LogLevel ParseLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "info" or "log" => LogLevel.Information,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information,
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…[truncated]");
}
