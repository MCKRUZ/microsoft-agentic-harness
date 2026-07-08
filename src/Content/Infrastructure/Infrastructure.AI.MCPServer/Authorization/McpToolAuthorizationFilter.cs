using System.Security.Claims;
using ModelContextProtocol.Protocol;

namespace Infrastructure.AI.MCPServer.Authorization;

/// <summary>
/// Per-tool-call authorization gate for inbound MCP <c>tools/call</c> requests.
/// Runs at the tool-dispatch layer as defense-in-depth behind the endpoint's
/// <c>RequireAuthorization()</c> — so a misconfigured or bypassed transport
/// guard does not leave tool execution unprotected.
/// </summary>
/// <remarks>
/// <para>
/// This is the baseline gate: unless the operator explicitly opted into anonymous
/// serving (<c>AppConfig:AI:MCP:Auth:AllowAnonymous=true</c>), every tool call must
/// carry an authenticated principal. The logic is extracted from the server-builder
/// wiring so it can be unit-tested in isolation.
/// </para>
/// <para>
/// Finer-grained, per-tool restriction is layered on top via standard
/// <see cref="Microsoft.AspNetCore.Authorization.AuthorizeAttribute"/> attributes on
/// individual tool methods, enabled by <c>AddAuthorizationFilters()</c>. A high-risk
/// tool is locked to a role with <c>[Authorize(Roles = "...")]</c> — no change to this
/// gate required.
/// </para>
/// </remarks>
internal static class McpToolAuthorizationFilter
{
    /// <summary>
    /// Evaluates whether an inbound tool call is permitted to proceed.
    /// </summary>
    /// <param name="authenticationRequired">
    /// True whenever the server enforces authentication — every mode except the
    /// explicit <c>AllowAnonymous=true</c> opt-in, where the gate is deliberately
    /// inert to match the operator's conscious choice to serve anonymously.
    /// </param>
    /// <param name="user">The caller principal carried on the request, if any.</param>
    /// <returns>
    /// <c>null</c> when the call may proceed; otherwise an error <see cref="CallToolResult"/>
    /// that short-circuits dispatch without invoking the tool.
    /// </returns>
    public static CallToolResult? Evaluate(bool authenticationRequired, ClaimsPrincipal? user)
    {
        if (authenticationRequired && user?.Identity?.IsAuthenticated != true)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "Authentication is required to invoke MCP tools." }]
            };
        }

        return null;
    }
}
