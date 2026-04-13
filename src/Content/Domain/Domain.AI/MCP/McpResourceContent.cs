namespace Domain.AI.MCP;

/// <summary>
/// The content of an MCP resource, returned by
/// the <c>IMcpResourceProvider.ReadAsync</c> operation.
/// </summary>
/// <param name="Uri">The resource URI that was read.</param>
/// <param name="Text">The UTF-8 text content of the resource file.</param>
/// <param name="MimeType">MIME type of the content. Defaults to <c>text/plain</c>.</param>
public sealed record McpResourceContent(
    string Uri,
    string Text,
    string MimeType = "text/plain");
