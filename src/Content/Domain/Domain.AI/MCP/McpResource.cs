namespace Domain.AI.MCP;

/// <summary>
/// Describes a resource exposed via the MCP <c>trace://</c> scheme.
/// Returned by the <c>IMcpResourceProvider.ListAsync</c> operation.
/// </summary>
/// <param name="Uri">The fully-qualified resource URI, e.g. <c>trace://{runId}/eval/task-1/output.json</c>.</param>
/// <param name="Name">Human-readable resource name, typically the file name.</param>
/// <param name="Description">Optional description of the resource's content.</param>
/// <param name="MimeType">MIME type hint for the consumer. Defaults to <c>text/plain</c>.</param>
public sealed record McpResource(
    string Uri,
    string Name,
    string? Description = null,
    string MimeType = "text/plain");
