using System.Text.Json;
using Application.AI.Common.Interfaces.GitOps;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools.GitOps;

/// <summary>
/// Read-only GitOps tool that returns the live <c>ClusterHealth</c> snapshot the
/// active <see cref="IGitOpsController"/> (Flux or Argo CD) reports — overall
/// status plus per-resource health — serialised as JSON.
/// </summary>
/// <remarks>
/// Observational only; the tool never mutates the cluster. It is the cheap
/// "is the cluster healthy right now?" probe that complements
/// <see cref="GitOpsDetectDriftTool"/>'s "does live match Git?" probe.
/// </remarks>
public sealed class GitOpsClusterHealthTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "cluster_health";

    private static readonly IReadOnlyList<string> Operations = ["get"];
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IGitOpsController _controller;

    /// <summary>Initialises a new instance of the <see cref="GitOpsClusterHealthTool"/> class.</summary>
    /// <param name="controller">The active GitOps controller resolved from configuration.</param>
    public GitOpsClusterHealthTool(IGitOpsController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        _controller = controller;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Returns the live cluster health snapshot (overall status + per-resource health) from the active GitOps controller. Read-only; returns JSON.";

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
        if (!string.Equals(operation, "get", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"Unknown operation: {operation}. Supported: get.");

        var result = await _controller.GetClusterHealthAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return ToolResult.Fail(string.Join("; ", result.Errors));

        return ToolResult.Ok(JsonSerializer.Serialize(result.Value, SerializerOptions));
    }
}
