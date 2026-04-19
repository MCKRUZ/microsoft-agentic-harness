namespace Presentation.AgentHub.DTOs;

/// <summary>
/// One entry in a batch POST from the browser logger. Shape is deliberately flat so the
/// server-side scope enricher can promote every field into the structured Serilog record.
/// </summary>
/// <param name="SessionId">Stable ID for one page-load; groups entries from the same tab.</param>
/// <param name="Level">One of <c>debug | info | warn | error</c>. Unknown values map to Information.</param>
/// <param name="Message">Formatted log message. May be multi-line for errors.</param>
/// <param name="Timestamp">Browser-side UTC timestamp of the event.</param>
/// <param name="Url">Page URL at the time of logging, for request-in-flight context.</param>
/// <param name="Stack">Optional stack trace, present on error/unhandled-rejection entries.</param>
public sealed record ClientLogEntry(
    string SessionId,
    string Level,
    string Message,
    DateTimeOffset Timestamp,
    string? Url,
    string? Stack);
