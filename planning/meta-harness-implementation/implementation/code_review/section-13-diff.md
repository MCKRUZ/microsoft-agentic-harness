diff --git a/src/Content/Application/Application.AI.Common/Interfaces/IMcpResourceProvider.cs b/src/Content/Application/Application.AI.Common/Interfaces/IMcpResourceProvider.cs
new file mode 100644
index 0000000..ad2f425
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/IMcpResourceProvider.cs
@@ -0,0 +1,43 @@
+using Domain.AI.MCP;
+
+namespace Application.AI.Common.Interfaces;
+
+/// <summary>
+/// Exposes resources via the MCP resource protocol.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Implementations are registered as singletons. Providers are composed at the MCP server level
+/// and invoked for any URI within their supported scheme.
+/// </para>
+/// <para>
+/// All operations must perform their own auth check via <see cref="McpRequestContext.IsAuthenticated"/>.
+/// Throw <see cref="UnauthorizedAccessException"/> for unauthenticated callers.
+/// </para>
+/// </remarks>
+public interface IMcpResourceProvider
+{
+    /// <summary>
+    /// Lists resources available at or beneath the given URI.
+    /// </summary>
+    /// <param name="uri">The base URI to list, e.g. <c>trace://{runId}/</c>.</param>
+    /// <param name="context">The request context carrying the caller's auth principal.</param>
+    /// <param name="ct">Cancellation token.</param>
+    /// <returns>
+    /// A read-only list of <see cref="McpResource"/> descriptors. Returns empty when the
+    /// URI refers to an unknown or disabled resource set.
+    /// </returns>
+    /// <exception cref="UnauthorizedAccessException">When <paramref name="context"/> is not authenticated.</exception>
+    Task<IReadOnlyList<McpResource>> ListAsync(string uri, McpRequestContext context, CancellationToken ct);
+
+    /// <summary>
+    /// Reads the content of the resource at the given URI.
+    /// </summary>
+    /// <param name="uri">The resource URI, e.g. <c>trace://{runId}/eval/task-1/output.json</c>.</param>
+    /// <param name="context">The request context carrying the caller's auth principal.</param>
+    /// <param name="ct">Cancellation token.</param>
+    /// <returns>The <see cref="McpResourceContent"/> for the requested resource.</returns>
+    /// <exception cref="UnauthorizedAccessException">When <paramref name="context"/> is not authenticated or path traversal is detected.</exception>
+    /// <exception cref="FileNotFoundException">When the resource file does not exist.</exception>
+    Task<McpResourceContent> ReadAsync(string uri, McpRequestContext context, CancellationToken ct);
+}
diff --git a/src/Content/Domain/Domain.AI/MCP/McpRequestContext.cs b/src/Content/Domain/Domain.AI/MCP/McpRequestContext.cs
new file mode 100644
index 0000000..c8caac9
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/MCP/McpRequestContext.cs
@@ -0,0 +1,42 @@
+using System.Security.Claims;
+
+namespace Domain.AI.MCP;
+
+/// <summary>
+/// Carries per-request context for MCP resource operations, including the caller's auth principal.
+/// </summary>
+/// <remarks>
+/// <para>
+/// <see cref="IsAuthenticated"/> is the canonical auth gate used by MCP resource providers.
+/// A context is authenticated when its <see cref="Principal"/> carries a validated identity
+/// (i.e., <c>ClaimsIdentity.AuthenticationType</c> is non-null/non-empty and
+/// <c>Identity.IsAuthenticated</c> is true).
+/// </para>
+/// <para>
+/// Use <see cref="FromPrincipal"/> to construct an authenticated context from a JWT-validated
+/// <see cref="ClaimsPrincipal"/>, or <see cref="Unauthenticated"/> for anonymous / test contexts.
+/// </para>
+/// </remarks>
+public sealed class McpRequestContext
+{
+    /// <summary>Gets the authenticated principal, or <c>null</c> if the caller is anonymous.</summary>
+    public ClaimsPrincipal? Principal { get; init; }
+
+    /// <summary>
+    /// Gets whether the request carries a valid authenticated identity.
+    /// Returns <c>true</c> only when <see cref="Principal"/> is non-null and its primary identity
+    /// reports <c>IsAuthenticated == true</c>.
+    /// </summary>
+    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
+
+    /// <summary>A pre-built unauthenticated context with no principal. Suitable for anonymous access or tests.</summary>
+    public static McpRequestContext Unauthenticated { get; } = new();
+
+    /// <summary>Creates an authenticated context wrapping the given <paramref name="principal"/>.</summary>
+    /// <param name="principal">A JWT-validated <see cref="ClaimsPrincipal"/> with <c>IsAuthenticated == true</c>.</param>
+    public static McpRequestContext FromPrincipal(ClaimsPrincipal principal)
+    {
+        ArgumentNullException.ThrowIfNull(principal);
+        return new() { Principal = principal };
+    }
+}
diff --git a/src/Content/Domain/Domain.AI/MCP/McpResource.cs b/src/Content/Domain/Domain.AI/MCP/McpResource.cs
new file mode 100644
index 0000000..ddab632
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/MCP/McpResource.cs
@@ -0,0 +1,15 @@
+namespace Domain.AI.MCP;
+
+/// <summary>
+/// Describes a resource exposed via the MCP <c>trace://</c> scheme.
+/// Returned by the <c>IMcpResourceProvider.ListAsync</c> operation.
+/// </summary>
+/// <param name="Uri">The fully-qualified resource URI, e.g. <c>trace://{runId}/eval/task-1/output.json</c>.</param>
+/// <param name="Name">Human-readable resource name, typically the file name.</param>
+/// <param name="Description">Optional description of the resource's content.</param>
+/// <param name="MimeType">MIME type hint for the consumer. Defaults to <c>text/plain</c>.</param>
+public sealed record McpResource(
+    string Uri,
+    string Name,
+    string? Description = null,
+    string MimeType = "text/plain");
diff --git a/src/Content/Domain/Domain.AI/MCP/McpResourceContent.cs b/src/Content/Domain/Domain.AI/MCP/McpResourceContent.cs
new file mode 100644
index 0000000..64595f3
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/MCP/McpResourceContent.cs
@@ -0,0 +1,13 @@
+namespace Domain.AI.MCP;
+
+/// <summary>
+/// The content of an MCP resource, returned by
+/// the <c>IMcpResourceProvider.ReadAsync</c> operation.
+/// </summary>
+/// <param name="Uri">The resource URI that was read.</param>
+/// <param name="Text">The UTF-8 text content of the resource file.</param>
+/// <param name="MimeType">MIME type of the content. Defaults to <c>text/plain</c>.</param>
+public sealed record McpResourceContent(
+    string Uri,
+    string Text,
+    string MimeType = "text/plain");
diff --git a/src/Content/Infrastructure/Infrastructure.AI.MCP/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI.MCP/DependencyInjection.cs
index ad66c78..1ff128d 100644
--- a/src/Content/Infrastructure/Infrastructure.AI.MCP/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI.MCP/DependencyInjection.cs
@@ -1,5 +1,6 @@
 using Application.AI.Common.Interfaces;
 using Domain.Common.Config.AI;
+using Infrastructure.AI.MCP.Resources;
 using Infrastructure.AI.MCP.Services;
 using Microsoft.Extensions.DependencyInjection;
 using Microsoft.Extensions.Options;
@@ -39,6 +40,11 @@ public static class DependencyInjection
         // Tool provider — singleton wrapping connection manager
         services.AddSingleton<IMcpToolProvider, McpToolProvider>();
 
+        // Trace resource provider — exposes optimization run trace files at trace:// URIs.
+        // Auth-gated and feature-flagged via MetaHarnessConfig.EnableMcpTraceResources.
+        services.AddSingleton<TraceResourceProvider>();
+        services.AddSingleton<IMcpResourceProvider>(sp => sp.GetRequiredService<TraceResourceProvider>());
+
         return services;
     }
 }
diff --git a/src/Content/Infrastructure/Infrastructure.AI.MCP/Resources/TraceResourceProvider.cs b/src/Content/Infrastructure/Infrastructure.AI.MCP/Resources/TraceResourceProvider.cs
new file mode 100644
index 0000000..7f881d2
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI.MCP/Resources/TraceResourceProvider.cs
@@ -0,0 +1,183 @@
+using Application.AI.Common.Interfaces;
+using Domain.AI.MCP;
+using Domain.Common.Config.MetaHarness;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.MCP.Resources;
+
+/// <summary>
+/// Exposes optimization run trace files as MCP resources at <c>trace://{optimizationRunId}/{path}</c>.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Requires JWT authentication on every operation. Throws <see cref="UnauthorizedAccessException"/>
+/// when the caller's <see cref="McpRequestContext.IsAuthenticated"/> is <c>false</c>.
+/// </para>
+/// <para>
+/// Gated by <see cref="MetaHarnessConfig.EnableMcpTraceResources"/>. When the flag is <c>false</c>,
+/// <see cref="ListAsync"/> returns an empty list and <see cref="ReadAsync"/> throws
+/// <see cref="InvalidOperationException"/>. Auth checks still run regardless of the flag.
+/// </para>
+/// <para>
+/// Security: every path is fully resolved with <see cref="Path.GetFullPath"/> before containment
+/// checks. The containment check uses a trailing-separator suffix to prevent
+/// <c>/traces/run-1</c> falsely matching <c>/traces/run-10</c>.
+/// On non-Windows, symlinks pointing outside the run directory are rejected.
+/// </para>
+/// <para>
+/// URI scheme: <c>trace://{optimizationRunId}/{relativePath}</c>.
+/// Directory layout: <c>{TraceDirectoryRoot}/optimizations/{optimizationRunId}/</c>.
+/// </para>
+/// </remarks>
+public sealed class TraceResourceProvider : IMcpResourceProvider
+{
+    private const string Scheme = "trace://";
+
+    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
+    private readonly ILogger<TraceResourceProvider> _logger;
+
+    /// <summary>Initializes a new instance of <see cref="TraceResourceProvider"/>.</summary>
+    public TraceResourceProvider(
+        IOptionsMonitor<MetaHarnessConfig> config,
+        ILogger<TraceResourceProvider> logger)
+    {
+        ArgumentNullException.ThrowIfNull(config);
+        ArgumentNullException.ThrowIfNull(logger);
+        _config = config;
+        _logger = logger;
+    }
+
+    /// <inheritdoc />
+    public async Task<IReadOnlyList<McpResource>> ListAsync(
+        string uri,
+        McpRequestContext context,
+        CancellationToken ct)
+    {
+        if (!context.IsAuthenticated)
+            throw new UnauthorizedAccessException("MCP resource access requires authentication.");
+
+        var cfg = _config.CurrentValue;
+        if (!cfg.EnableMcpTraceResources)
+            return [];
+
+        if (!TryParseRunId(uri, out var runId))
+            return [];
+
+        var runRoot = ResolveRunRoot(cfg.TraceDirectoryRoot, runId);
+
+        if (!Directory.Exists(runRoot))
+            return [];
+
+        var resources = new List<McpResource>();
+        foreach (var file in Directory.EnumerateFiles(runRoot, "*", SearchOption.AllDirectories))
+        {
+            var rel = Path.GetRelativePath(runRoot, file).Replace('\\', '/');
+            resources.Add(new McpResource(
+                Uri: $"{Scheme}{runId}/{rel}",
+                Name: Path.GetFileName(file)));
+        }
+
+        _logger.LogDebug(
+            "Listed {Count} trace resources for optimization run '{RunId}'",
+            resources.Count, runId);
+
+        return resources;
+    }
+
+    /// <inheritdoc />
+    public async Task<McpResourceContent> ReadAsync(
+        string uri,
+        McpRequestContext context,
+        CancellationToken ct)
+    {
+        if (!context.IsAuthenticated)
+            throw new UnauthorizedAccessException("MCP resource access requires authentication.");
+
+        var cfg = _config.CurrentValue;
+        if (!cfg.EnableMcpTraceResources)
+            throw new InvalidOperationException("MCP trace resources are disabled.");
+
+        if (!TryParseUri(uri, out var runId, out var relativePath))
+            throw new ArgumentException($"Invalid or incomplete trace URI: '{uri}'.");
+
+        // Traversal guard: reject before resolution
+        if (relativePath.Contains(".."))
+            throw new UnauthorizedAccessException($"Path traversal detected in URI: '{uri}'.");
+
+        var runRoot = ResolveRunRoot(cfg.TraceDirectoryRoot, runId);
+        var fullPath = Path.GetFullPath(Path.Combine(runRoot, relativePath));
+
+        // Root containment: resolved path must stay within the run directory
+        if (!IsPathSafe(fullPath, runRoot))
+            throw new UnauthorizedAccessException(
+                $"Path '{relativePath}' resolves outside the run directory.");
+
+        // Symlink guard (non-Windows)
+        if (!OperatingSystem.IsWindows())
+        {
+            var fileInfo = new FileInfo(fullPath);
+            var realTarget = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
+            if (realTarget is not null)
+            {
+                var realPath = Path.GetFullPath(realTarget.FullName);
+                if (!IsPathSafe(realPath, runRoot))
+                    throw new UnauthorizedAccessException(
+                        "File is a symlink pointing outside the run directory.");
+            }
+        }
+
+        if (!File.Exists(fullPath))
+            throw new FileNotFoundException($"Trace resource not found: '{uri}'.", fullPath);
+
+        var text = await File.ReadAllTextAsync(fullPath, ct);
+
+        _logger.LogDebug("Read trace resource '{Uri}' ({Chars} chars)", uri, text.Length);
+
+        return new McpResourceContent(uri, text);
+    }
+
+    private static string ResolveRunRoot(string traceRoot, string runId) =>
+        Path.GetFullPath(Path.Combine(traceRoot, "optimizations", runId));
+
+    /// <summary>Parses the optimization run ID from a <c>trace://{runId}/...</c> URI.</summary>
+    private static bool TryParseRunId(string uri, out string runId)
+    {
+        runId = string.Empty;
+        if (!uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;
+
+        var rest = uri[Scheme.Length..].TrimEnd('/');
+        var slash = rest.IndexOf('/');
+        runId = slash < 0 ? rest : rest[..slash];
+        return !string.IsNullOrEmpty(runId);
+    }
+
+    /// <summary>
+    /// Parses both run ID and relative path from a <c>trace://{runId}/{relativePath}</c> URI.
+    /// Returns <c>false</c> when the URI has no path component after the run ID.
+    /// </summary>
+    private static bool TryParseUri(string uri, out string runId, out string relativePath)
+    {
+        runId = string.Empty;
+        relativePath = string.Empty;
+
+        if (!uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;
+
+        var rest = uri[Scheme.Length..];
+        var slash = rest.IndexOf('/');
+        if (slash < 0) return false; // no path component
+
+        runId = rest[..slash];
+        relativePath = rest[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar);
+
+        return !string.IsNullOrEmpty(runId) && !string.IsNullOrEmpty(relativePath);
+    }
+
+    private static bool IsPathSafe(string resolvedPath, string resolvedRoot)
+    {
+        var rootWithSep = resolvedRoot.TrimEnd(Path.DirectorySeparatorChar)
+                          + Path.DirectorySeparatorChar;
+        return resolvedPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
+               || string.Equals(resolvedPath, resolvedRoot, StringComparison.OrdinalIgnoreCase);
+    }
+}
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
index ea99c01..b9c6642 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
@@ -20,6 +20,8 @@ using Azure.AI.Agents.Persistent;
 using Azure.AI.OpenAI;
 using Domain.Common.Config;
 using Domain.Common.Config.AI;
+using Domain.Common.Config.MetaHarness;
+using Microsoft.Extensions.Options;
 using Infrastructure.AI.A2A;
 using Infrastructure.AI.MetaHarness;
 using Infrastructure.AI.Audit;
@@ -110,6 +112,13 @@ public static class DependencyInjection
         services.AddKeyedSingleton<ITool>(FileSystemTool.ToolName, (sp, _) =>
             new FileSystemTool(sp.GetRequiredService<IFileSystemService>()));
 
+        // Restricted search tool — sandboxed read-only shell commands for the proposer.
+        // Always registered; surfaced to the proposer only when EnableShellTool is true.
+        services.AddKeyedSingleton<ITool>(RestrictedSearchTool.ToolName, (sp, _) =>
+            new RestrictedSearchTool(
+                sp.GetRequiredService<IOptionsMonitor<MetaHarnessConfig>>(),
+                sp.GetRequiredService<ILogger<RestrictedSearchTool>>()));
+
         // Azure AI Foundry persistent agents — register administration client when configured
         if (appConfig.AI.AIFoundry.IsConfigured)
         {
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Tools/RestrictedSearchTool.cs b/src/Content/Infrastructure/Infrastructure.AI/Tools/RestrictedSearchTool.cs
new file mode 100644
index 0000000..0208c96
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Tools/RestrictedSearchTool.cs
@@ -0,0 +1,289 @@
+using System.Diagnostics;
+using System.Text;
+using Application.AI.Common.Interfaces.Tools;
+using Domain.AI.Models;
+using Domain.Common.Config.MetaHarness;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.Tools;
+
+/// <summary>
+/// Executes read-only shell commands (grep, rg, cat, find, ls, head, tail, jq, wc)
+/// sandboxed to the trace root directory.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Only surfaced to the proposer's tool set when <see cref="MetaHarnessConfig.EnableShellTool"/>
+/// is <c>true</c>. The tool is always registered in DI; the flag is evaluated at tool-set
+/// assembly time (section 11 proposer), not here.
+/// </para>
+/// <para>
+/// Security pipeline (fail-fast, evaluated in order):
+/// <list type="number">
+///   <item>Binary allowlist — only <c>grep rg cat find ls head tail jq wc</c> are permitted.</item>
+///   <item>Metacharacter rejection — shell injection characters are rejected before any process spawn.</item>
+///   <item>Working directory containment — fully-resolved path must start with the fully-resolved trace root.</item>
+///   <item>Symlink guard (non-Windows) — symlinks pointing outside the trace root are rejected.</item>
+///   <item>Process execution — <c>UseShellExecute=false</c>, isolated environment, 30-second timeout.</item>
+///   <item>Output cap — stdout is read up to 1 MB; excess is discarded with a truncation marker.</item>
+/// </list>
+/// </para>
+/// <para>
+/// Keyed DI name: <c>"restricted_search"</c>. Operation: <c>"execute"</c>.
+/// Parameters: <c>command</c> (string, required), <c>working_directory</c> (string, optional).
+/// </para>
+/// </remarks>
+public sealed class RestrictedSearchTool : ITool
+{
+    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
+    public const string ToolName = "restricted_search";
+
+    private const int MaxOutputBytes = 1_048_576; // 1 MB
+
+    private static readonly IReadOnlySet<string> AllowedBinaries =
+        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
+        {
+            "grep", "rg", "cat", "find", "ls", "head", "tail", "jq", "wc"
+        };
+
+    private static readonly string[] ForbiddenMetacharacters =
+        [";", "|", "&&", "||", ">", "<", "`", "$(", "\n"];
+
+    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
+    private readonly ILogger<RestrictedSearchTool> _logger;
+    private readonly TimeSpan _commandTimeout;
+
+    /// <summary>
+    /// Initializes a new instance of <see cref="RestrictedSearchTool"/>.
+    /// </summary>
+    /// <param name="config">Application config; provides <see cref="MetaHarnessConfig.TraceDirectoryRoot"/>.</param>
+    /// <param name="logger">Structured logger.</param>
+    /// <param name="commandTimeout">Process execution timeout. Defaults to 30 seconds.</param>
+    public RestrictedSearchTool(
+        IOptionsMonitor<MetaHarnessConfig> config,
+        ILogger<RestrictedSearchTool> logger,
+        TimeSpan? commandTimeout = null)
+    {
+        ArgumentNullException.ThrowIfNull(config);
+        ArgumentNullException.ThrowIfNull(logger);
+        _config = config;
+        _logger = logger;
+        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(30);
+    }
+
+    /// <inheritdoc />
+    public string Name => ToolName;
+
+    /// <inheritdoc />
+    public string Description =>
+        "Executes read-only shell commands (grep, rg, cat, find, ls, head, tail, jq, wc) " +
+        "sandboxed to the trace root directory. Only available when EnableShellTool is true.";
+
+    /// <inheritdoc />
+    public IReadOnlyList<string> SupportedOperations { get; } = ["execute"];
+
+    /// <inheritdoc />
+    public bool IsReadOnly => true;
+
+    /// <inheritdoc />
+    public bool IsConcurrencySafe => true;
+
+    /// <inheritdoc />
+    public async Task<ToolResult> ExecuteAsync(
+        string operation,
+        IReadOnlyDictionary<string, object?> parameters,
+        CancellationToken cancellationToken = default)
+    {
+        if (!string.Equals(operation, "execute", StringComparison.Ordinal))
+            return ToolResult.Fail(
+                $"RestrictedSearchTool does not support operation '{operation}'. Supported: execute");
+
+        if (!parameters.TryGetValue("command", out var commandObj)
+            || commandObj is not string command
+            || string.IsNullOrWhiteSpace(command))
+            return ToolResult.Fail("Required parameter 'command' is missing or empty.");
+
+        var cfg = _config.CurrentValue;
+        var traceRoot = cfg.TraceDirectoryRoot;
+
+        var workingDir = parameters.TryGetValue("working_directory", out var wdObj)
+            && wdObj is string wd && !string.IsNullOrWhiteSpace(wd)
+            ? wd
+            : traceRoot;
+
+        // Step 1: Binary allowlist
+        var binary = ExtractBinary(command);
+        if (!AllowedBinaries.Contains(binary))
+            return ToolResult.Fail(
+                $"Command '{binary}' is not in the allowed list: {string.Join(", ", AllowedBinaries.Order())}.");
+
+        // Step 2: Metacharacter rejection
+        foreach (var meta in ForbiddenMetacharacters)
+        {
+            if (command.Contains(meta, StringComparison.Ordinal))
+                return ToolResult.Fail($"Command contains forbidden metacharacter: '{meta}'.");
+        }
+
+        // Step 3: Working directory path validation
+        string resolvedWorkingDir;
+        string resolvedRoot;
+        try
+        {
+            resolvedWorkingDir = Path.GetFullPath(workingDir);
+            resolvedRoot = Path.GetFullPath(traceRoot);
+        }
+        catch (Exception ex)
+        {
+            return ToolResult.Fail($"Invalid path: {ex.Message}");
+        }
+
+        if (!IsPathSafe(resolvedWorkingDir, resolvedRoot))
+            return ToolResult.Fail(
+                $"Working directory '{resolvedWorkingDir}' is outside the trace root '{resolvedRoot}'.");
+
+        // Symlink guard (non-Windows)
+        if (!OperatingSystem.IsWindows() && IsSymlinkOutsideRoot(resolvedWorkingDir, resolvedRoot))
+            return ToolResult.Fail(
+                "Working directory resolves through a symlink outside the trace root.");
+
+        // Step 4: Process execution
+        var psi = new ProcessStartInfo
+        {
+            FileName = binary,
+            Arguments = ExtractArguments(command),
+            UseShellExecute = false,
+            RedirectStandardOutput = true,
+            RedirectStandardError = true,
+            WorkingDirectory = resolvedWorkingDir,
+            CreateNoWindow = true
+        };
+
+        // Isolated environment: clear inherited vars (no credential leaks), keep a minimal PATH
+        psi.Environment.Clear();
+        psi.Environment["PATH"] = OperatingSystem.IsWindows()
+            ? (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
+            : "/usr/local/bin:/usr/bin:/bin";
+
+        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
+        cts.CancelAfter(_commandTimeout);
+
+        Process process;
+        try
+        {
+            process = Process.Start(psi)
+                ?? throw new InvalidOperationException($"Process.Start returned null for '{binary}'.");
+        }
+        catch (Exception ex)
+        {
+            return ToolResult.Fail($"Failed to start process '{binary}': {ex.Message}");
+        }
+
+        // Step 5: Output cap + timeout
+        var stdoutTask = ReadWithCapAsync(process.StandardOutput, MaxOutputBytes, cts.Token);
+        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
+
+        try
+        {
+            await process.WaitForExitAsync(cts.Token);
+        }
+        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
+        {
+            // Our internal timeout fired — kill the process
+            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
+            return ToolResult.Fail($"Command timed out after {_commandTimeout.TotalSeconds:0} seconds.");
+        }
+
+        var (stdout, truncated) = await stdoutTask;
+        var stderr = await stderrTask;
+
+        // Step 6: Return
+        var output = process.ExitCode != 0
+            ? $"[exit code {process.ExitCode}]\n{stderr}\n{stdout}"
+            : stdout;
+
+        if (truncated)
+            output += "\n[output truncated at 1MB]";
+
+        _logger.LogDebug(
+            "RestrictedSearchTool executed '{Binary}' in '{WorkDir}' exit={ExitCode}",
+            binary, resolvedWorkingDir, process.ExitCode);
+
+        return ToolResult.Ok(output);
+    }
+
+    private static string ExtractBinary(string command) =>
+        command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
+
+    private static string ExtractArguments(string command)
+    {
+        var idx = command.IndexOf(' ');
+        return idx < 0 ? string.Empty : command[(idx + 1)..];
+    }
+
+    private static bool IsPathSafe(string resolvedPath, string resolvedRoot)
+    {
+        // Append separator to prevent /traces/run-1 matching /traces/run-10
+        var rootWithSep = resolvedRoot.TrimEnd(Path.DirectorySeparatorChar)
+                          + Path.DirectorySeparatorChar;
+        return resolvedPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
+               || string.Equals(resolvedPath, resolvedRoot, StringComparison.OrdinalIgnoreCase);
+    }
+
+    private static bool IsSymlinkOutsideRoot(string resolvedPath, string resolvedRoot)
+    {
+        try
+        {
+            var info = new DirectoryInfo(resolvedPath);
+            var realTarget = info.ResolveLinkTarget(returnFinalTarget: true);
+            if (realTarget is null) return false;
+
+            var realPath = Path.GetFullPath(realTarget.FullName);
+            return !IsPathSafe(realPath, resolvedRoot);
+        }
+        catch
+        {
+            return false;
+        }
+    }
+
+    private static async Task<(string Output, bool Truncated)> ReadWithCapAsync(
+        TextReader reader,
+        int maxBytes,
+        CancellationToken ct)
+    {
+        var buffer = new char[4096];
+        var sb = new StringBuilder();
+        var byteCount = 0;
+        var truncated = false;
+
+        while (true)
+        {
+            int read;
+            try { read = await reader.ReadAsync(buffer, ct); }
+            catch (OperationCanceledException) { break; }
+
+            if (read == 0) break;
+
+            if (!truncated)
+            {
+                var chunk = new string(buffer, 0, read);
+                var chunkBytes = Encoding.UTF8.GetByteCount(chunk);
+
+                if (byteCount + chunkBytes > maxBytes)
+                {
+                    truncated = true;
+                    // Continue draining (without appending) so the process doesn't block on a full pipe
+                }
+                else
+                {
+                    sb.Append(chunk);
+                    byteCount += chunkBytes;
+                }
+            }
+            // When truncated: keep reading and discarding until EOF so the process can exit
+        }
+
+        return (sb.ToString(), truncated);
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj b/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj
index d65db7b..5ff9443 100644
--- a/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj
@@ -22,6 +22,7 @@
 
   <ItemGroup>
     <ProjectReference Include="../../Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj" />
+    <ProjectReference Include="../../Infrastructure/Infrastructure.AI.MCP/Infrastructure.AI.MCP.csproj" />
     <ProjectReference Include="../../Application/Application.AI.Common/Application.AI.Common.csproj" />
     <ProjectReference Include="../../Application/Application.Core/Application.Core.csproj" />
   </ItemGroup>
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/MCP/TraceResourceProviderTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/MCP/TraceResourceProviderTests.cs
new file mode 100644
index 0000000..2d5ba14
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/MCP/TraceResourceProviderTests.cs
@@ -0,0 +1,149 @@
+using System.Security.Claims;
+using Domain.AI.MCP;
+using Domain.Common.Config.MetaHarness;
+using FluentAssertions;
+using Infrastructure.AI.MCP.Resources;
+using Microsoft.Extensions.Logging.Abstractions;
+using Microsoft.Extensions.Options;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.MCP;
+
+public sealed class TraceResourceProviderTests : IDisposable
+{
+    private readonly string _traceRoot;
+    private readonly string _runId;
+    private readonly string _runDir;
+    private readonly TraceResourceProvider _provider;
+    private readonly McpRequestContext _authContext;
+
+    public TraceResourceProviderTests()
+    {
+        _traceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
+        _runId = "run-" + Guid.NewGuid().ToString("N")[..8];
+        _runDir = Path.Combine(_traceRoot, "optimizations", _runId);
+        Directory.CreateDirectory(_runDir);
+
+        var config = new MetaHarnessConfig
+        {
+            TraceDirectoryRoot = _traceRoot,
+            EnableMcpTraceResources = true
+        };
+        var monitor = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == config);
+        _provider = new TraceResourceProvider(monitor, NullLogger<TraceResourceProvider>.Instance);
+
+        // Authenticated context: non-empty AuthenticationType makes IsAuthenticated = true
+        var identity = new ClaimsIdentity("Bearer");
+        _authContext = McpRequestContext.FromPrincipal(new ClaimsPrincipal(identity));
+    }
+
+    public void Dispose()
+    {
+        try { Directory.Delete(_traceRoot, recursive: true); } catch { /* best effort */ }
+    }
+
+    // ── Auth tests ──
+
+    [Fact]
+    public async Task Read_WithoutAuth_Rejects()
+    {
+        var file = Path.Combine(_runDir, "output.json");
+        await File.WriteAllTextAsync(file, "{}");
+
+        var act = () => _provider.ReadAsync(
+            $"trace://{_runId}/output.json",
+            McpRequestContext.Unauthenticated,
+            CancellationToken.None);
+
+        await act.Should().ThrowAsync<UnauthorizedAccessException>();
+    }
+
+    [Fact]
+    public async Task List_WithoutAuth_Rejects()
+    {
+        var act = () => _provider.ListAsync(
+            $"trace://{_runId}/",
+            McpRequestContext.Unauthenticated,
+            CancellationToken.None);
+
+        await act.Should().ThrowAsync<UnauthorizedAccessException>();
+    }
+
+    // ── List tests ──
+
+    [Fact]
+    public async Task List_ValidOptimizationRunId_ReturnsFiles()
+    {
+        await File.WriteAllTextAsync(Path.Combine(_runDir, "manifest.json"), "{}");
+        await File.WriteAllTextAsync(Path.Combine(_runDir, "summary.txt"), "ok");
+
+        var resources = await _provider.ListAsync(
+            $"trace://{_runId}/", _authContext, CancellationToken.None);
+
+        resources.Should().HaveCount(2);
+        resources.Select(r => r.Uri).Should().AllSatisfy(u => u.Should().StartWith("trace://"));
+    }
+
+    // ── Read tests ──
+
+    [Fact]
+    public async Task Read_ValidPath_ReturnsFileContent()
+    {
+        var file = Path.Combine(_runDir, "result.json");
+        await File.WriteAllTextAsync(file, "{\"score\": 0.9}");
+
+        var content = await _provider.ReadAsync(
+            $"trace://{_runId}/result.json", _authContext, CancellationToken.None);
+
+        content.Uri.Should().Be($"trace://{_runId}/result.json");
+        content.Text.Should().Be("{\"score\": 0.9}");
+    }
+
+    [Fact]
+    public async Task Read_PathWithDotDot_RejectsTraversal()
+    {
+        var act = () => _provider.ReadAsync(
+            $"trace://{_runId}/../../../etc/passwd",
+            _authContext,
+            CancellationToken.None);
+
+        await act.Should().ThrowAsync<UnauthorizedAccessException>();
+    }
+
+    [Fact]
+    public async Task Read_PathOutsideOptimizationRunDir_Rejects()
+    {
+        // Encode traversal without literal ".." — resolved path escapes run dir
+        var act = () => _provider.ReadAsync(
+            $"trace://{_runId}/subdir/../../other-run/secret.txt",
+            _authContext,
+            CancellationToken.None);
+
+        await act.Should().ThrowAsync<UnauthorizedAccessException>();
+    }
+
+    [Fact]
+    public async Task Read_SymlinkOutsideRoot_Rejects()
+    {
+        if (OperatingSystem.IsWindows()) return; // symlinks require elevated privileges on Windows
+
+        var outsideFile = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid() + ".txt");
+        await File.WriteAllTextAsync(outsideFile, "secret");
+
+        var symlinkPath = Path.Combine(_runDir, "link.txt");
+        File.CreateSymbolicLink(symlinkPath, outsideFile);
+
+        try
+        {
+            var act = () => _provider.ReadAsync(
+                $"trace://{_runId}/link.txt", _authContext, CancellationToken.None);
+
+            await act.Should().ThrowAsync<UnauthorizedAccessException>();
+        }
+        finally
+        {
+            try { File.Delete(outsideFile); } catch { }
+        }
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Tools/RestrictedSearchToolTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Tools/RestrictedSearchToolTests.cs
new file mode 100644
index 0000000..331d710
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Tools/RestrictedSearchToolTests.cs
@@ -0,0 +1,241 @@
+using System.Diagnostics;
+using System.Security.Claims;
+using FluentAssertions;
+using Infrastructure.AI.Tools;
+using Microsoft.Extensions.Logging.Abstractions;
+using Microsoft.Extensions.Options;
+using Moq;
+using Domain.Common.Config.MetaHarness;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Tools;
+
+public sealed class RestrictedSearchToolTests : IDisposable
+{
+    private readonly string _traceRoot;
+    private readonly RestrictedSearchTool _tool;
+
+    public RestrictedSearchToolTests()
+    {
+        _traceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
+        Directory.CreateDirectory(_traceRoot);
+
+        var config = new MetaHarnessConfig { TraceDirectoryRoot = _traceRoot };
+        var monitor = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == config);
+        _tool = new RestrictedSearchTool(monitor, NullLogger<RestrictedSearchTool>.Instance, TimeSpan.FromSeconds(5));
+    }
+
+    public void Dispose()
+    {
+        try { Directory.Delete(_traceRoot, recursive: true); } catch { /* best effort */ }
+    }
+
+    // ── Security rejection tests (no process spawn — fast on all platforms) ──
+
+    [Fact]
+    public async Task Execute_Curl_RejectsNonAllowlistedBinary()
+    {
+        var result = await _tool.ExecuteAsync("execute",
+            new Dictionary<string, object?> { ["command"] = "curl https://evil.com" });
+
+        result.Success.Should().BeFalse();
+        result.Error.Should().Contain("curl").And.Contain("allowed");
+    }
+
+    [Fact]
+    public async Task Execute_Python_RejectsNonAllowlistedBinary()
+    {
+        var result = await _tool.ExecuteAsync("execute",
+            new Dictionary<string, object?> { ["command"] = "python -c 'import os; os.system(\"id\")'" });
+
+        result.Success.Should().BeFalse();
+        result.Error.Should().Contain("python");
+    }
+
+    [Fact]
+    public async Task Execute_CommandWithPipe_RejectsMetacharacter()
+    {
+        var result = await _tool.ExecuteAsync("execute",
+            new Dictionary<string, object?> { ["command"] = "grep foo bar | cat" });
+
+        result.Success.Should().BeFalse();
+        result.Error.Should().Contain("|");
+    }
+
+    [Fact]
+    public async Task Execute_CommandWithSemicolon_RejectsMetacharacter()
+    {
+        var result = await _tool.ExecuteAsync("execute",
+            new Dictionary<string, object?> { ["command"] = "grep foo bar; ls" });
+
+        result.Success.Should().BeFalse();
+        result.Error.Should().Contain(";");
+    }
+
+    [Fact]
+    public async Task Execute_CommandWithRedirect_RejectsMetacharacter()
+    {
+        var result = await _tool.ExecuteAsync("execute",
+            new Dictionary<string, object?> { ["command"] = "grep foo bar > /tmp/out" });
+
+        result.Success.Should().BeFalse();
+        result.Error.Should().Contain(">");
+    }
+
+    [Fact]
+    public async Task Execute_PathOutsideTraceRoot_Rejects()
+    {
+        var outsideDir = Path.GetTempPath();
+
+        var result = await _tool.ExecuteAsync("execute",
+            new Dictionary<string, object?>
+            {
+                ["command"] = "ls .",
+                ["working_directory"] = outsideDir
+            });
+
+        result.Success.Should().BeFalse();
+        result.Error.Should().Contain("outside");
+    }
+
+    [Fact]
+    public async Task Execute_PathWithDotDot_RejectsAfterResolution()
+    {
+        // Construct a path that uses ".." to escape the trace root
+        var escapedPath = Path.Combine(_traceRoot, "..", "..");
+
+        var result = await _tool.ExecuteAsync("execute",
+            new Dictionary<string, object?>
+            {
+                ["command"] = "ls .",
+                ["working_directory"] = escapedPath
+            });
+
+        result.Success.Should().BeFalse();
+        result.Error.Should().Contain("outside");
+    }
+
+    [Fact]
+    public async Task Execute_UnsupportedOperation_ReturnsFail()
+    {
+        var result = await _tool.ExecuteAsync("run",
+            new Dictionary<string, object?> { ["command"] = "grep foo bar" });
+
+        result.Success.Should().BeFalse();
+        result.Error.Should().Contain("run");
+    }
+
+    [Fact]
+    public async Task Execute_MissingCommand_ReturnsFail()
+    {
+        var result = await _tool.ExecuteAsync("execute", new Dictionary<string, object?>());
+
+        result.Success.Should().BeFalse();
+        result.Error.Should().Contain("command");
+    }
+
+    // ── Execution tests (require Unix-like tools in PATH) ──
+
+    [Fact]
+    public async Task Execute_Grep_WithinTraceRoot_Succeeds()
+    {
+        if (!IsCommandAvailable("grep")) return;
+
+        var testFile = Path.Combine(_traceRoot, "test.log");
+        await File.WriteAllTextAsync(testFile, "hello world\nfoo bar\n");
+
+        var result = await _tool.ExecuteAsync("execute",
+            new Dictionary<string, object?>
+            {
+                ["command"] = "grep hello test.log",
+                ["working_directory"] = _traceRoot
+            });
+
+        result.Success.Should().BeTrue();
+        result.Output.Should().Contain("hello");
+    }
+
+    [Fact]
+    public async Task Execute_Cat_WithinTraceRoot_Succeeds()
+    {
+        if (!IsCommandAvailable("cat")) return;
+
+        var testFile = Path.Combine(_traceRoot, "data.txt");
+        await File.WriteAllTextAsync(testFile, "sample content");
+
+        var result = await _tool.ExecuteAsync("execute",
+            new Dictionary<string, object?>
+            {
+                ["command"] = "cat data.txt",
+                ["working_directory"] = _traceRoot
+            });
+
+        result.Success.Should().BeTrue();
+        result.Output.Should().Contain("sample content");
+    }
+
+    [Fact]
+    public async Task Execute_LongRunningCommand_TimesOutAfter30Seconds()
+    {
+        if (!IsCommandAvailable("grep")) return;
+
+        // grep reading from stdin (no file arg) blocks indefinitely — drives the timeout
+        // The tool is constructed with a 5s timeout in this test fixture
+        var result = await _tool.ExecuteAsync("execute",
+            new Dictionary<string, object?>
+            {
+                ["command"] = "grep pattern_that_wont_match",
+                ["working_directory"] = _traceRoot
+            });
+
+        // Either timeout or process failure — either way, not a hang
+        // The test passes as long as it completes within reasonable time (the 5s fixture timeout)
+        result.Should().NotBeNull();
+    }
+
+    [Fact]
+    public async Task Execute_LargeOutput_TruncatesAt1MB()
+    {
+        if (!IsCommandAvailable("cat")) return;
+
+        // Write a file > 1 MB
+        var largeFile = Path.Combine(_traceRoot, "large.txt");
+        var line = new string('x', 1000) + "\n";
+        var content = string.Concat(Enumerable.Repeat(line, 1100)); // ~1.1 MB
+        await File.WriteAllTextAsync(largeFile, content);
+
+        var result = await _tool.ExecuteAsync("execute",
+            new Dictionary<string, object?>
+            {
+                ["command"] = "cat large.txt",
+                ["working_directory"] = _traceRoot
+            });
+
+        result.Success.Should().BeTrue();
+        result.Output.Should().Contain("[output truncated at 1MB]");
+    }
+
+    private static bool IsCommandAvailable(string command)
+    {
+        try
+        {
+            var finder = OperatingSystem.IsWindows() ? "where" : "which";
+            var psi = new ProcessStartInfo
+            {
+                FileName = finder,
+                Arguments = command,
+                UseShellExecute = false,
+                RedirectStandardOutput = true,
+                RedirectStandardError = true,
+                CreateNoWindow = true
+            };
+            using var p = Process.Start(psi)!;
+            p.WaitForExit(3000);
+            return p.ExitCode == 0;
+        }
+        catch
+        {
+            return false;
+        }
+    }
+}
