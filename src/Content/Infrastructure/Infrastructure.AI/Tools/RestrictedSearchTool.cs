using System.Diagnostics;
using System.Text;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;
using Domain.Common.Config.MetaHarness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Executes read-only shell commands (grep, rg, cat, find, ls, head, tail, jq, wc)
/// sandboxed to the trace root directory.
/// </summary>
/// <remarks>
/// <para>
/// Only surfaced to the proposer's tool set when <see cref="MetaHarnessConfig.EnableShellTool"/>
/// is <c>true</c>. The tool is always registered in DI; the flag is evaluated at tool-set
/// assembly time (section 11 proposer), not here.
/// </para>
/// <para>
/// Security pipeline (fail-fast, evaluated in order):
/// <list type="number">
///   <item>Binary allowlist — only <c>grep rg cat find ls head tail jq wc</c> are permitted.</item>
///   <item>Metacharacter rejection — shell injection characters are rejected before any process spawn.</item>
///   <item>Working directory containment — fully-resolved path must start with the fully-resolved trace root.</item>
///   <item>Symlink guard (non-Windows) — symlinks pointing outside the trace root are rejected.</item>
///   <item>Process execution — <c>UseShellExecute=false</c>, isolated environment, 30-second timeout.</item>
///   <item>Output cap — stdout is read up to 1 MB; excess is discarded with a truncation marker.</item>
/// </list>
/// </para>
/// <para>
/// Keyed DI name: <c>"restricted_search"</c>. Operation: <c>"execute"</c>.
/// Parameters: <c>command</c> (string, required), <c>working_directory</c> (string, optional).
/// </para>
/// </remarks>
public sealed class RestrictedSearchTool : ITool
{
    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
    public const string ToolName = "restricted_search";

    private const int MaxOutputBytes = 1_048_576; // 1 MB

    private static readonly IReadOnlySet<string> AllowedBinaries =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "grep", "rg", "cat", "find", "ls", "head", "tail", "jq", "wc"
        };

    private static readonly string[] ForbiddenMetacharacters =
        [";", "|", "&&", "||", ">", "<", "`", "$(", "\n"];

    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
    private readonly ILogger<RestrictedSearchTool> _logger;
    private readonly TimeSpan _commandTimeout;

    /// <summary>
    /// Initializes a new instance of <see cref="RestrictedSearchTool"/>.
    /// </summary>
    /// <param name="config">Application config; provides <see cref="MetaHarnessConfig.TraceDirectoryRoot"/>.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="commandTimeout">Process execution timeout. Defaults to 30 seconds.</param>
    public RestrictedSearchTool(
        IOptionsMonitor<MetaHarnessConfig> config,
        ILogger<RestrictedSearchTool> logger,
        TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Executes read-only shell commands (grep, rg, cat, find, ls, head, tail, jq, wc) " +
        "sandboxed to the trace root directory. Only available when EnableShellTool is true.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations { get; } = ["execute"];

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public bool IsConcurrencySafe => true;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, "execute", StringComparison.Ordinal))
            return ToolResult.Fail(
                $"RestrictedSearchTool does not support operation '{operation}'. Supported: execute");

        if (!parameters.TryGetValue("command", out var commandObj)
            || commandObj is not string command
            || string.IsNullOrWhiteSpace(command))
            return ToolResult.Fail("Required parameter 'command' is missing or empty.");

        var cfg = _config.CurrentValue;
        var traceRoot = cfg.TraceDirectoryRoot;

        var workingDir = parameters.TryGetValue("working_directory", out var wdObj)
            && wdObj is string wd && !string.IsNullOrWhiteSpace(wd)
            ? wd
            : traceRoot;

        // Step 1: Binary allowlist
        var binary = ExtractBinary(command);
        if (!AllowedBinaries.Contains(binary))
            return ToolResult.Fail(
                $"Command '{binary}' is not in the allowed list: {string.Join(", ", AllowedBinaries.Order())}.");

        // Step 2: Metacharacter rejection
        foreach (var meta in ForbiddenMetacharacters)
        {
            if (command.Contains(meta, StringComparison.Ordinal))
                return ToolResult.Fail($"Command contains forbidden metacharacter: '{meta}'.");
        }

        // Step 3: Working directory path validation
        string resolvedWorkingDir;
        string resolvedRoot;
        try
        {
            resolvedWorkingDir = Path.GetFullPath(workingDir);
            resolvedRoot = Path.GetFullPath(traceRoot);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Invalid path: {ex.Message}");
        }

        if (!IsPathSafe(resolvedWorkingDir, resolvedRoot))
            return ToolResult.Fail(
                $"Working directory '{resolvedWorkingDir}' is outside the trace root '{resolvedRoot}'.");

        // Symlink guard (non-Windows)
        if (!OperatingSystem.IsWindows() && IsSymlinkOutsideRoot(resolvedWorkingDir, resolvedRoot))
            return ToolResult.Fail(
                "Working directory resolves through a symlink outside the trace root.");

        // Step 4: Process execution
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            Arguments = ExtractArguments(command),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = resolvedWorkingDir,
            CreateNoWindow = true
        };

        // Isolated environment: clear inherited vars (no credential leaks), keep a minimal PATH
        psi.Environment.Clear();
        psi.Environment["PATH"] = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            : "/usr/local/bin:/usr/bin:/bin";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_commandTimeout);

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Process.Start returned null for '{binary}'.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to start process '{binary}': {ex.Message}");
        }

        // Step 5: Output cap + timeout (both stdout and stderr capped to prevent OOM)
        var stdoutTask = ReadWithCapAsync(process.StandardOutput, MaxOutputBytes, cts.Token);
        var stderrTask = ReadWithCapAsync(process.StandardError, 65_536, cts.Token); // 64 KB stderr cap

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our internal timeout fired — kill the process
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return ToolResult.Fail($"Command timed out after {_commandTimeout.TotalSeconds:0} seconds.");
        }

        var (stdout, truncated) = await stdoutTask;
        var (stderr, _) = await stderrTask;

        // Step 6: Return
        var output = process.ExitCode != 0
            ? $"[exit code {process.ExitCode}]\n{stderr}\n{stdout}"
            : stdout;

        if (truncated)
            output += "\n[output truncated at 1MB]";

        _logger.LogDebug(
            "RestrictedSearchTool executed '{Binary}' in '{WorkDir}' exit={ExitCode}",
            binary, resolvedWorkingDir, process.ExitCode);

        return ToolResult.Ok(output);
    }

    private static string ExtractBinary(string command) =>
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    private static string ExtractArguments(string command)
    {
        var idx = command.IndexOf(' ');
        return idx < 0 ? string.Empty : command[(idx + 1)..];
    }

    private static bool IsPathSafe(string resolvedPath, string resolvedRoot)
    {
        // Append separator to prevent /traces/run-1 matching /traces/run-10
        var rootWithSep = resolvedRoot.TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        return resolvedPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
               || string.Equals(resolvedPath, resolvedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSymlinkOutsideRoot(string resolvedPath, string resolvedRoot)
    {
        try
        {
            var info = new DirectoryInfo(resolvedPath);
            var realTarget = info.ResolveLinkTarget(returnFinalTarget: true);
            if (realTarget is null) return false;

            var realPath = Path.GetFullPath(realTarget.FullName);
            return !IsPathSafe(realPath, resolvedRoot);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(string Output, bool Truncated)> ReadWithCapAsync(
        TextReader reader,
        int maxBytes,
        CancellationToken ct)
    {
        var buffer = new char[4096];
        var sb = new StringBuilder();
        var byteCount = 0;
        var truncated = false;

        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buffer, ct); }
            catch (OperationCanceledException) { break; }

            if (read == 0) break;

            if (!truncated)
            {
                var chunk = new string(buffer, 0, read);
                var chunkBytes = Encoding.UTF8.GetByteCount(chunk);

                if (byteCount + chunkBytes > maxBytes)
                {
                    truncated = true;
                    // Continue draining (without appending) so the process doesn't block on a full pipe
                }
                else
                {
                    sb.Append(chunk);
                    byteCount += chunkBytes;
                }
            }
            // When truncated: keep reading and discarding until EOF so the process can exit
        }

        return (sb.ToString(), truncated);
    }
}
