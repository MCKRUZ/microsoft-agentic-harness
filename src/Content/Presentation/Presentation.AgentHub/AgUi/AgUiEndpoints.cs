namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Maps the AG-UI protocol SSE endpoint. Accepts a <see cref="RunAgentInput"/>
/// via HTTP POST and streams the agent response as Server-Sent Events.
/// </summary>
public static class AgUiEndpoints
{
    /// <summary>
    /// Maps <c>POST /ag-ui/run</c> with authorization required.
    /// </summary>
    public static IEndpointRouteBuilder MapAgUiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/ag-ui/run", HandleRunAsync)
            .RequireAuthorization()
            .Accepts<RunAgentInput>("application/json")
            .WithName("AgUiRun")
            .WithDescription("AG-UI protocol streaming endpoint");

        return endpoints;
    }

    private static async Task HandleRunAsync(
        HttpContext httpContext,
        RunAgentInput input,
        AgUiRunHandler handler,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.ThreadId) ||
            string.IsNullOrWhiteSpace(input.RunId) ||
            input.Messages is not { Count: > 0 })
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(
                new { error = "threadId, runId, and at least one message are required" }, ct);
            return;
        }

        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        var writer = new AgUiEventWriter(httpContext.Response.Body);
        await handler.HandleRunAsync(input, writer, httpContext.User, ct);
    }
}
