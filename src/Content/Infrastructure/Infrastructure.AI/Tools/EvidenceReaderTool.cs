using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Governance;

namespace Infrastructure.AI.Tools;

/// <summary>
/// A read-only view of one evidence folder for a proposer session (#843, #847): read, list, search and
/// exists, and no operation that writes. Used by the skill-training proposer and the MetaHarness proposer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a tool of its own.</b> The proposer must be able to read a large trajectory without the
/// harness truncating it to fit a prompt, and must not be able to change anything. The obvious grant,
/// <c>file_system</c>, cannot give that: it also writes, and a skill's declared <c>operations</c> list is
/// not enforced when a tool is called, so declaring <c>[read, list]</c> would not make a session
/// read-only. The only dependable way to withhold a write is to hand over a tool that has none.
/// </para>
/// <para>
/// <b>Confined twice.</b> The tool is built over a <see cref="IFileSystemService"/> whose only allowed
/// base paths are the configured evidence roots (the skill-training evidence root and the MetaHarness trace root), so a path outside them (including a symlink or a <c>..</c> out
/// of it) is refused by the same hardened guard the general file tool uses. Inside the root it is narrowed
/// again to the one folder published by <see cref="EvidenceReadScope"/> (a training step's evidence, or a
/// MetaHarness run's directory): a root holds a folder per step or run, and concurrent runs and trees left by
/// a crash share it, so root-level confinement alone would let a session steered by text in a trajectory read
/// another run's evidence. With no folder
/// published the tool refuses everything.
/// </para>
/// <para>
/// <b>Reads are paged, not clipped.</b> A <c>read</c> returns at most <c>max_chars</c> characters
/// (default <see cref="DefaultReadChars"/>) starting at <c>offset</c>, and says when there is more and
/// where to continue, so a long trajectory is fully reachable without ever entering the context whole.
/// </para>
/// </remarks>
public sealed class EvidenceReaderTool : ITool
{
    /// <summary>The tool name matching keyed DI registration and the skill-optimizer declaration.</summary>
    public const string ToolName = "evidence_reader";

    /// <summary>Characters a <c>read</c> returns when <c>max_chars</c> is not given.</summary>
    public const int DefaultReadChars = 60_000;

    /// <summary>The most characters a single <c>read</c> may return.</summary>
    public const int MaxReadChars = 200_000;

    private static readonly IReadOnlyList<string> Operations = ["read", "list", "search", "exists"];

    private static readonly IReadOnlyDictionary<string, ResourceParameterKind> PathOnly =
        new Dictionary<string, ResourceParameterKind> { ["path"] = ResourceParameterKind.Path };

    private readonly IFileSystemService _evidence;

    /// <summary>Initializes the tool over a file service confined to the evidence root.</summary>
    /// <param name="evidence">A file service whose only allowed base path is the evidence root.</param>
    public EvidenceReaderTool(IFileSystemService evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        _evidence = evidence;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Reads the evidence for one proposer step: list a folder (path, optional pattern), read a file " +
        "(path, optional offset and max_chars; a long file is paged, and the reply says where to continue), " +
        "search files (path, search_term, optional pattern), or check a path exists. It cannot write.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public bool IsConcurrencySafe => true;

    /// <inheritdoc />
    /// <remarks>
    /// Not offered through the direct tool-invocation surface: the evidence holds an agent's trajectories,
    /// and the only intended reader is the proposer session that was built for it.
    /// </remarks>
    public bool IsDirectlyInvocable => false;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.Low;

    /// <inheritdoc />
    /// <remarks>
    /// A source of untrusted input: a trajectory is tool output and model text this harness did not author,
    /// and the proposer session reads it. It has no sink capability of its own.
    /// </remarks>
    public ToolCompositionCapability Capabilities => ToolCompositionCapability.IngestsUntrustedInput;

    /// <inheritdoc />
    public ToolCapability RequiredCapabilities => ToolCapability.FileRead;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>> ResourceParametersByOperation { get; } =
        Operations.ToDictionary(op => op, _ => PathOnly);

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var requested = parameters.TryGetValue("path", out var raw) && raw is string text ? text : null;
            if (requested is not null && !EvidenceReadScope.Allows(requested))
                return ToolResult.Fail("Access denied: path is outside this step's evidence.");

            return operation.ToLowerInvariant() switch
            {
                "read" => await ReadAsync(parameters, cancellationToken),
                "list" => await ListAsync(parameters, cancellationToken),
                "search" => await SearchAsync(parameters, cancellationToken),
                "exists" => ToolResult.Ok(await _evidence.ExistsAsync(Required(parameters, "path"), cancellationToken) ? "true" : "false"),
                _ => ToolResult.Fail($"Unknown operation: {operation}. Supported: {string.Join(", ", Operations)}")
            };
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Fail("Access denied: path is outside the evidence tree.");
        }
        catch (FileNotFoundException)
        {
            return ToolResult.Fail("File not found.");
        }
        catch (DirectoryNotFoundException)
        {
            return ToolResult.Fail("Directory not found.");
        }
        catch (IOException)
        {
            return ToolResult.Fail("I/O error: file may exceed size limits or be inaccessible.");
        }
        catch (ArgumentException)
        {
            return ToolResult.Fail("Invalid path or parameters.");
        }
    }

    private async Task<ToolResult> ReadAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        var content = await _evidence.ReadFileAsync(Required(parameters, "path"), ct);

        var offset = Math.Clamp(OptionalInt(parameters, "offset") ?? 0, 0, content.Length);
        var wanted = Math.Clamp(OptionalInt(parameters, "max_chars") ?? DefaultReadChars, 1, MaxReadChars);
        var length = Math.Min(wanted, content.Length - offset);

        var page = content.Substring(offset, length);
        var next = offset + length;
        return next < content.Length
            ? ToolResult.Ok($"{page}\n[{content.Length - next:N0} more characters; read again with offset={next}]")
            : ToolResult.Ok(page);
    }

    private async Task<ToolResult> ListAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        var entries = await _evidence.ListDirectoryAsync(Required(parameters, "path"), Optional(parameters, "pattern"), ct);
        return ToolResult.Ok(string.Join('\n', entries));
    }

    private async Task<ToolResult> SearchAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        var results = await _evidence.SearchFilesAsync(
            Required(parameters, "path"), Required(parameters, "search_term"), Optional(parameters, "pattern"), ct);

        if (results.Count == 0)
            return ToolResult.Ok("No matches found.");

        return ToolResult.Ok(string.Join('\n', results.Select(r => r.LineNumber.HasValue
            ? $"{r.FilePath}:{r.LineNumber}: {r.Snippet}"
            : $"{r.FilePath}: {r.Snippet}")));
    }

    private static string Required(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is not string s || string.IsNullOrWhiteSpace(s))
            throw new ArgumentException($"Required parameter '{key}' is missing or empty.");
        return s;
    }

    private static string? Optional(IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.TryGetValue(key, out var value) && value is string s && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? OptionalInt(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            double d when d is >= int.MinValue and <= int.MaxValue => (int)d,
            string s when int.TryParse(s, out var parsed) => parsed,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } e when e.TryGetInt32(out var j) => j,
            _ => null
        };
    }
}
