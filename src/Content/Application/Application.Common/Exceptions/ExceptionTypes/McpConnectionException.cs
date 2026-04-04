using Application.Common.Exceptions;

namespace Application.Common.Exceptions.ExceptionTypes;

/// <summary>
/// Represents an exception thrown when a connection to an MCP (Model Context Protocol) server
/// fails or a transport-level error occurs. This exception provides structured context about
/// which server and transport were involved.
/// </summary>
/// <remarks>
/// MCP servers dynamically extend the agent's tool surface. Connection failures need to be
/// distinguishable from generic connectivity errors so the agent can decide whether to retry,
/// degrade gracefully, or report the unavailability of specific capabilities. Common scenarios include:
/// <list type="bullet">
///   <item><description>MCP server process failed to start (stdio transport)</description></item>
///   <item><description>SSE or HTTP endpoint is unreachable or returned a non-200 status</description></item>
///   <item><description>WebSocket handshake failed or connection was dropped</description></item>
///   <item><description>OAuth token refresh failed for an authenticated MCP server</description></item>
///   <item><description>MCP protocol version mismatch between client and server</description></item>
///   <item><description>Server discovery succeeded but tool listing timed out</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// catch (HttpRequestException ex)
/// {
///     throw new McpConnectionException("local-tools", "http", ex);
/// }
/// </code>
/// </example>
public sealed class McpConnectionException : ApplicationExceptionBase
{
    /// <summary>
    /// Gets the name or identifier of the MCP server that failed, if specified.
    /// </summary>
    /// <value>The server name (e.g., "local-tools", "remote-search"), or <c>null</c> if not provided.</value>
    public string? ServerName { get; }

    /// <summary>
    /// Gets the transport type that was in use when the connection failed, if specified.
    /// </summary>
    /// <value>
    /// The transport identifier (e.g., "stdio", "sse", "http", "websocket"),
    /// or <c>null</c> if not provided.
    /// </value>
    public string? Transport { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="McpConnectionException"/> class
    /// with a default error message.
    /// </summary>
    public McpConnectionException()
        : base("Failed to connect to an MCP server.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="McpConnectionException"/> class
    /// with a custom error message.
    /// </summary>
    /// <param name="message">A message describing the MCP connection failure.</param>
    public McpConnectionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="McpConnectionException"/> class
    /// with a custom error message and a reference to the inner exception that caused it.
    /// </summary>
    /// <param name="message">A message describing the MCP connection failure.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public McpConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="McpConnectionException"/> class
    /// with structured context about the failed MCP connection.
    /// </summary>
    /// <param name="serverName">The name or identifier of the MCP server that failed.</param>
    /// <param name="transport">
    /// The transport type in use (e.g., "stdio", "sse", "http", "websocket").
    /// Pass <c>null</c> if the transport is unknown or not yet established.
    /// </param>
    /// <param name="innerException">The optional underlying exception that caused the failure.</param>
    /// <example>
    /// <code>
    /// throw new McpConnectionException("local-tools", "stdio");
    /// // Message: "Failed to connect to MCP server 'local-tools' via 'stdio' transport."
    ///
    /// throw new McpConnectionException("remote-search", null);
    /// // Message: "Failed to connect to MCP server 'remote-search'."
    /// </code>
    /// </example>
    public McpConnectionException(string serverName, string? transport, Exception? innerException = null)
        : base(
            transport is not null
                ? $"Failed to connect to MCP server '{serverName}' via '{transport}' transport."
                : $"Failed to connect to MCP server '{serverName}'.",
            innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ServerName = serverName;
        Transport = transport;
    }
}
