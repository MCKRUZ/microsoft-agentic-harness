using System.Text.Json;
using Application.AI.Common.Interfaces.Bundles;
using Domain.Common.Config.AI.BundleExecution;
using Domain.Common.Config.AI.MCP;
using Infrastructure.AI.Plugins;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Bundles;

/// <summary>
/// The bundle-owned <c>stdio</c> (local-command, sandboxed) MCP server registration gate (#371) —
/// split from <c>BundleStagingService.cs</c> per this repo's partial-class convention once the main
/// file grew past its line budget absorbing this capability alongside the pre-existing remote
/// (http/sse) one from #370. <see cref="TryAddOwnedServer"/> and the server-name safety check the
/// remote path also uses stay in the main file — only what is exclusively stdio-specific moved here.
/// </summary>
public sealed partial class BundleStagingService
{
    /// <summary>
    /// Registers a bundle-owned <c>stdio</c> (local-command) MCP server — reachable only when every one
    /// of these holds, checked in order so the log line always names the FIRST reason a caller could fix:
    /// <list type="number">
    /// <item><description>The manifest declares <c>"type": "stdio"</c> <strong>explicitly</strong>. A
    /// server that merely defaulted to stdio (absent or unrecognized <c>type</c> —
    /// <see cref="McpServerDefinitionBuilder"/>'s <c>ParseType</c> maps both to
    /// <see cref="McpServerType.Stdio"/>) is rejected via <see cref="LogStdioRejected"/> exactly as
    /// before this capability existed — a bundle author who typos a remote transport must not silently
    /// land on a sandboxed process launch instead.</description></item>
    /// <item><description><see cref="BundleStdioMcpServersConfig.Enabled"/> is on — a separate opt-in
    /// from <see cref="BundleExecutionConfig.AllowBundleDeclaredMcpServers"/>, which governs remote
    /// servers only.</description></item>
    /// <item><description>An operator has configured <see cref="BundleStdioMcpServersConfig.ContainerImage"/>
    /// — otherwise every bundle stdio server would run in the harness's own default (.NET runtime) image,
    /// which cannot run most MCP servers, so registering one would be pointless.</description></item>
    /// <item><description>The sandbox subsystem itself is enabled
    /// (<c>AppConfig.AI.SandboxCapabilities.Enabled</c>) — both session factories refuse to start when
    /// it is off; checking here means staging never registers something that can never run.</description></item>
    /// <item><description>The manifest declares a non-empty <c>command</c> —
    /// <see cref="McpServerDefinitionBuilder"/> does not itself enforce this for stdio the way it enforces
    /// a URL for remote servers.</description></item>
    /// <item><description>The bundle has not already reached
    /// <see cref="BundleStdioMcpServersConfig.MaxServersPerBundle"/> — each registered stdio server
    /// gets its own sandbox container per concurrent run against the bundle's staged handle (#455;
    /// before that, one shared container for the life of the handle), so this caps how many distinct
    /// server <em>names</em> a bundle can declare, not how many containers can be live for it at
    /// once.</description></item>
    /// </list>
    /// On success, tags <see cref="McpServerDefinition.SandboxSeedDirectory"/> with the bundle's staged
    /// root directory <strong>before</strong> <see cref="IBundleOwnedMcpServerRegistry.TryAdd"/> — tagged
    /// at the moment of creation, per this repo's provenance-tracking pattern, rather than re-derived from
    /// the server's name later — so <c>McpConnectionManager.StartSandboxedStdioSessionAsync</c> can seed
    /// the server's sandbox workspace with the bundle's own files.
    /// <para>
    /// The seed is always <see cref="ServerRegistrationContext.BundleDir"/> — the WHOLE bundle's staged root, not the
    /// specific plugin manifest that declared this server — even when the declaration lives in a
    /// nested <c>plugins/&lt;name&gt;/mcp.json</c>. This was a deliberate choice, not an omission: the
    /// server may legitimately need sibling content elsewhere in the bundle. The consequence a bundle
    /// author must know: the container's working directory is the bundle root, so <c>command</c>/
    /// <c>args</c> declared in a nested manifest must reference their own file(s) with a path relative
    /// to the bundle root (e.g. <c>plugins/my-plugin/server.js</c>), not relative to the manifest's own
    /// directory.
    /// </para>
    /// </summary>
    private bool TryRegisterStdioServer(
        ServerRegistrationContext context, string namespacedName, JsonProperty serverProp, McpServerDefinition definition,
        int stdioServerCount)
    {
        if (!McpServerDefinitionBuilder.IsExplicitType(serverProp.Value, McpServerType.Stdio))
        {
            LogStdioRejected(context.BundleId, serverProp.Name);
            return false;
        }

        var stdioConfig = context.BundleExecution.StdioMcpServers;

        // Short-circuiting || means the FIRST failing check logs and stops evaluation — the same
        // "log the first reason" ordering the inline checks this replaced had, just delegated one
        // guard per method instead of five inline blocks in one body.
        if (!IsStdioCapabilityEnabled(context.BundleId, serverProp.Name, stdioConfig)
            || !HasConfiguredContainerImage(context.BundleId, serverProp.Name, stdioConfig)
            || !IsSandboxSubsystemEnabled(context.BundleId, serverProp.Name, context.SandboxEnabled)
            || !HasNonEmptyCommand(context.BundleId, serverProp.Name, definition)
            || !IsWithinPerBundleStdioServerCap(context.BundleId, serverProp.Name, stdioServerCount, stdioConfig))
        {
            return false;
        }

        definition.SandboxSeedDirectory = context.BundleDir;

        return TryAddOwnedServer(context.BundleId, namespacedName, serverProp.Name, definition);
    }

    private bool IsStdioCapabilityEnabled(string bundleId, string serverName, BundleStdioMcpServersConfig stdioConfig)
    {
        if (stdioConfig.Enabled)
            return true;

        _logger.LogInformation(
            "Bundle {BundleId}: MCP server '{ServerName}' explicitly declares a stdio transport, but " +
            "AppConfig:AI:BundleExecution:StdioMcpServers:Enabled is disabled — rejected, not registered. " +
            "Set it to true to enable sandboxed bundle-owned stdio MCP servers.",
            bundleId, serverName);
        return false;
    }

    private bool HasConfiguredContainerImage(string bundleId, string serverName, BundleStdioMcpServersConfig stdioConfig)
    {
        if (!string.IsNullOrEmpty(stdioConfig.ContainerImage))
            return true;

        _logger.LogWarning(
            "Bundle {BundleId}: MCP server '{ServerName}' explicitly declares a stdio transport, but no " +
            "AppConfig:AI:BundleExecution:StdioMcpServers:ContainerImage is configured — rejected, not " +
            "registered. The capability stays inert until an operator sets a runtime image.",
            bundleId, serverName);
        return false;
    }

    private bool IsSandboxSubsystemEnabled(string bundleId, string serverName, bool sandboxEnabled)
    {
        if (sandboxEnabled)
            return true;

        _logger.LogWarning(
            "Bundle {BundleId}: MCP server '{ServerName}' explicitly declares a stdio transport, but the " +
            "sandbox subsystem (AppConfig:AI:SandboxCapabilities:Enabled) is disabled — rejected, not registered.",
            bundleId, serverName);
        return false;
    }

    private bool HasNonEmptyCommand(string bundleId, string serverName, McpServerDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.Command))
            return true;

        _logger.LogWarning(
            "Bundle {BundleId}: MCP server '{ServerName}' declares a stdio transport with no command — " +
            "rejected, not registered.",
            bundleId, serverName);
        return false;
    }

    private bool IsWithinPerBundleStdioServerCap(
        string bundleId, string serverName, int stdioServerCount, BundleStdioMcpServersConfig stdioConfig)
    {
        if (stdioServerCount < stdioConfig.MaxServersPerBundle)
            return true;

        _logger.LogWarning(
            "Bundle {BundleId}: MCP server '{ServerName}' exceeds the per-bundle stdio server cap of " +
            "{MaxServersPerBundle} — rejected, not registered.",
            bundleId, serverName, stdioConfig.MaxServersPerBundle);
        return false;
    }

    // A bundle is untrusted, uploader-supplied content. A server whose manifest never explicitly declared
    // "type": "stdio" — an absent or unrecognized value, which McpServerDefinitionBuilder.ParseType
    // defaults to Stdio too — is rejected rather than treated as an intentional local-command request: a
    // bundle author who typos a remote transport must not silently land on a sandboxed process launch.
    // An EXPLICIT stdio declaration is handled by TryRegisterStdioServer instead, not here (#371).
    private void LogStdioRejected(string bundleId, string serverName)
    {
        _logger.LogWarning(
            "Bundle {BundleId}: MCP server '{ServerName}' resolved to a stdio (local-command) transport " +
            "without an explicit 'type': 'stdio' declaration — rejected, not registered. The transport " +
            "either defaulted to stdio because 'type' was missing or unrecognized (only 'http'/'sse'/'stdio' " +
            "are recognized), which this host never treats as an intentional stdio request.",
            bundleId, serverName);
    }
}
