using Presentation.Common.Extensions;
using Application.AI.Common.Interfaces.AI;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Maps the AG-UI protocol SSE endpoints: the streaming run endpoint and the out-of-band
/// tool-result resume endpoint that completes a mid-run client round-trip.
/// </summary>
public static class AgUiEndpoints
{
    /// <summary>
    /// Maps <c>POST /ag-ui/run</c> (streaming) and <c>POST /ag-ui/tool-result</c> (resume),
    /// both with authorization required.
    /// </summary>
    public static IEndpointRouteBuilder MapAgUiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/ag-ui/run", HandleRunAsync)
            .RequireAuthorization()
            .Accepts<RunAgentInput>("application/json")
            .WithName("AgUiRun")
            .WithDescription("AG-UI protocol streaming endpoint");

        endpoints.MapPost("/ag-ui/tool-result", HandleToolResultAsync)
            .RequireAuthorization()
            .Accepts<ToolResultInput>("application/json")
            .WithName("AgUiToolResult")
            .WithDescription("Completes a mid-run AG-UI client round-trip tool call");

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

        using var writer = new AgUiEventWriter(httpContext.Response.Body);
        await handler.HandleRunAsync(input, writer, httpContext.User, ct);
    }

    /// <summary>
    /// Completes a pending client round-trip tool call. The browser POSTs the result of an action it
    /// performed in response to a <c>TOOL_CALL_*</c> sequence; this unblocks the server-side proxy
    /// tool awaiting on the <see cref="PendingToolCallRegistry"/> and the agent run resumes.
    /// </summary>
    /// <remarks>
    /// Ownership is enforced in depth: the caller must own the conversation named by
    /// <see cref="ToolResultInput.ThreadId"/> (checked here), and the pending call must itself be bound
    /// to that same thread (checked in <see cref="PendingToolCallRegistry.TryComplete"/>). Both must
    /// hold, so a caller cannot inject a result into another user's in-flight run even if they learn its
    /// <see cref="ToolResultInput.CallId"/>.
    /// </remarks>
    private static async Task<IResult> HandleToolResultAsync(
        HttpContext httpContext,
        ToolResultInput input,
        IConversationStore conversationStore,
        PendingToolCallRegistry registry,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.ThreadId) ||
            string.IsNullOrWhiteSpace(input.CallId) ||
            input.Result is null)
        {
            return Results.BadRequest(new { error = "threadId, callId, and result are required" });
        }

        var callerId = httpContext.User.GetUserIdOrNull();
        if (callerId is null)
            return Results.Unauthorized();

        var record = await conversationStore.GetAsync(input.ThreadId, ct);
        if (record is null)
            return Results.NotFound(new { error = "Conversation not found." });

        if (record.UserId != callerId)
            return Results.Forbid();

        // false ⇒ no call with this id is pending for this thread (already completed, timed out, never
        // existed, or registered under a different conversation).
        return registry.TryComplete(input.CallId, input.ThreadId, input.Result)
            ? Results.Ok()
            : Results.NotFound(new { error = "No pending tool call with the supplied callId." });
    }
}

/// <summary>
/// Request body for <c>POST /ag-ui/tool-result</c>: the browser's result for a mid-run client
/// round-trip tool call.
/// </summary>
/// <param name="ThreadId">The conversation the call belongs to (ownership is verified against it).</param>
/// <param name="CallId">The <c>toolCallId</c> echoed from the <c>TOOL_CALL_START</c> event.</param>
/// <param name="Result">The result payload the agent should observe (typically a short JSON/text summary).</param>
public sealed record ToolResultInput(string ThreadId, string CallId, string Result);
