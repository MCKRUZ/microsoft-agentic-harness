using System.Text.Json;
using Application.AI.Common.Interfaces.GitOps;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools.GitOps;

/// <summary>
/// Read-only GitOps tool that runs a K8sGPT root-cause analysis of the cluster
/// via <see cref="IK8sGptMcpClient"/> and returns the structured findings as
/// JSON. Backs the <c>gitops-cluster-debug</c> skill.
/// </summary>
/// <remarks>
/// <para>
/// Parameters (all optional): <c>namespace</c> (string — scope; omit for all
/// namespaces), <c>filters</c> (comma-separated string or string array of
/// resource kinds, e.g. <c>"Deployment,Pod"</c>), and <c>explain</c> (bool —
/// request a natural-language explanation, default true).
/// </para>
/// <para>
/// The K8sGPT analysis surface is read-only by contract: the client narrows the
/// MCP server to analysis-only regardless of what it advertises, so this tool
/// can never mutate the cluster.
/// </para>
/// </remarks>
public sealed class K8sGptAnalyzeTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "k8sgpt_analyze";

    private static readonly IReadOnlyList<string> Operations = ["analyze"];
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IK8sGptMcpClient _client;

    /// <summary>Initialises a new instance of the <see cref="K8sGptAnalyzeTool"/> class.</summary>
    /// <param name="client">Client-side abstraction over the K8sGPT MCP server.</param>
    public K8sGptAnalyzeTool(IK8sGptMcpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Runs a K8sGPT root-cause analysis of the cluster (optionally scoped by namespace and resource-kind filters) and returns structured findings as JSON. Read-only — never mutates the cluster.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.Low;

    /// <inheritdoc />
    public bool IsConcurrencySafe => true;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, "analyze", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"Unknown operation: {operation}. Supported: analyze.");

        var request = new K8sGptAnalysisRequest
        {
            Namespace = ReadNamespace(parameters),
            Filters = ReadFilters(parameters),
            Explain = ReadExplain(parameters),
        };

        var result = await _client.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return ToolResult.Fail(string.Join("; ", result.Errors));

        return ToolResult.Ok(JsonSerializer.Serialize(result.Value, SerializerOptions));
    }

    private static string? ReadNamespace(IReadOnlyDictionary<string, object?> parameters)
        => parameters.TryGetValue("namespace", out var v) && v is string s && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;

    private static bool ReadExplain(IReadOnlyDictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("explain", out var v) || v is null)
            return true;

        return v switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            _ => true,
        };
    }

    private static IReadOnlyList<string> ReadFilters(IReadOnlyDictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("filters", out var v) || v is null)
            return [];

        return v switch
        {
            IEnumerable<string> list => list.Where(static x => !string.IsNullOrWhiteSpace(x)).ToArray(),
            string csv => SplitCsv(csv),
            JsonElement { ValueKind: JsonValueKind.Array } arr =>
                arr.EnumerateArray()
                   .Select(static e => e.GetString())
                   .Where(static x => !string.IsNullOrWhiteSpace(x))
                   .Select(static x => x!)
                   .ToArray(),
            JsonElement { ValueKind: JsonValueKind.String } str => SplitCsv(str.GetString() ?? string.Empty),
            _ => [],
        };
    }

    private static string[] SplitCsv(string csv)
        => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
