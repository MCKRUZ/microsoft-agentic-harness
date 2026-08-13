using System.Collections.Concurrent;

namespace Domain.Common.Config.AI.MCP;

/// <summary>
/// Client-side configuration for external MCP servers the harness connects to.
/// Each entry defines a server the agent can consume tools from.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by server name (e.g., "filesystem", "github", "remote-tools").
/// The key becomes the server identifier used in <c>IMcpToolProvider.GetToolsAsync(serverName)</c>.
/// </para>
/// <para>
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> rather than a plain dictionary: server registration
/// is no longer a startup-only write. A host-installed plugin registers its own namespaced servers at
/// load time (<c>PluginLoader</c>), concurrently with reads that hold an enumerator open across network
/// I/O (<c>McpToolProvider.GetToolByNameAsync</c>) — a plain <see cref="Dictionary{TKey,TValue}"/> is
/// unsafe under that mix of concurrent read/write. (A staged bundle's own servers are registered into
/// the separate <c>BundleOwnedMcpServerRegistry</c> instead, not here — see its own doc comment.)
/// Confirmed to bind identically to a plain dictionary from real <c>IConfiguration</c>
/// (<c>McpServersConfigBindingTests.Bind_JsonConfiguredServers_AlsoPopulatesConcurrentDictionary</c>)
/// — an earlier, incorrectly-shaped binding test wrongly suggested otherwise; see that test's remarks.
/// </para>
/// </remarks>
public class McpServersConfig
{
    /// <summary>
    /// Gets or sets the dictionary of MCP server definitions keyed by server name.
    /// </summary>
    public ConcurrentDictionary<string, McpServerDefinition> Servers { get; set; } = new();
}
