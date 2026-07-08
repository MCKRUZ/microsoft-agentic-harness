namespace Infrastructure.AI.MCPServer.Authentication;

/// <summary>
/// Logs a prominent warning at host startup while the MCP server is running with
/// authentication explicitly disabled (<c>AppConfig:AI:MCP:Auth:AllowAnonymous=true</c>).
/// Registered only on that opt-in path so the warning cannot be missed in logs.
/// </summary>
internal sealed class McpAnonymousModeStartupWarning(
    ILogger<McpAnonymousModeStartupWarning> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "MCP SERVER AUTHENTICATION IS DISABLED — running in explicit ANONYMOUS mode. " +
            "Every MCP tool, prompt, and resource is served without credentials because " +
            "AppConfig:AI:MCP:Auth:AllowAnonymous=true. This is only acceptable for local " +
            "development. Configure AppConfig:AI:MCP:Auth (ApiKey, Bearer, or Entra) and " +
            "remove AllowAnonymous before exposing this server on any network.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
