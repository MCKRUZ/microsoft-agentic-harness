using System.Text.Json;
using Application.AI.Common.Interfaces.GitOps;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools.GitOps;

/// <summary>
/// Read-only GitOps tool that asks the active <see cref="IGitOpsController"/>
/// (Flux or Argo CD, per <c>AppConfig.AI.GitOps.ActiveController</c>) to detect
/// configuration drift between the desired Git state and the live cluster, then
/// returns the controller-neutral <c>DriftReport</c> as JSON.
/// </summary>
/// <remarks>
/// <para>
/// The tool never mutates the cluster or the Git repository — drift detection is
/// purely observational. To act on detected drift the agent must call
/// <see cref="GitOpsProposeRemediationTool"/>, which routes a fix through the
/// <c>ChangeProposal</c> gate pipeline.
/// </para>
/// </remarks>
public sealed class GitOpsDetectDriftTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "detect_drift";

    private static readonly IReadOnlyList<string> Operations = ["detect"];
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IGitOpsController _controller;

    /// <summary>Initialises a new instance of the <see cref="GitOpsDetectDriftTool"/> class.</summary>
    /// <param name="controller">The active GitOps controller resolved from configuration.</param>
    public GitOpsDetectDriftTool(IGitOpsController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        _controller = controller;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Detects configuration drift between desired Git state and the live cluster via the active GitOps controller (Flux or Argo CD). Read-only; returns a drift report as JSON.";

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
        if (!string.Equals(operation, "detect", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"Unknown operation: {operation}. Supported: detect.");

        var result = await _controller.DetectDriftAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return ToolResult.Fail(string.Join("; ", result.Errors));

        return ToolResult.Ok(JsonSerializer.Serialize(result.Value, SerializerOptions));
    }
}
