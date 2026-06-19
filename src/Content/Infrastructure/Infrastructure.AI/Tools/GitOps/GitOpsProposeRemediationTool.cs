using System.Text.Json;
using Application.AI.Common.Interfaces.GitOps;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools.GitOps;

/// <summary>
/// State-changing GitOps tool that detects drift, asks the active controller to
/// propose a Git-side remediation, and dispatches that plan through the
/// <c>ChangeProposal</c> gate pipeline. It never writes to the cluster or the
/// repository directly — the remediation becomes a proposal subject to gates and
/// approval, exactly like every other state change in the harness.
/// </summary>
/// <remarks>
/// <para>
/// The full chain is: <see cref="IGitOpsController.DetectDriftAsync"/> →
/// <see cref="IGitOpsController.ProposeRemediationAsync"/> →
/// <see cref="IGitOpsRemediationDispatcher.DispatchAsync"/>. When the cluster is
/// already in sync the tool short-circuits and reports that no remediation is
/// needed rather than submitting an empty proposal.
/// </para>
/// <para>
/// Marked not read-only and not concurrency-safe: dispatching a proposal mutates
/// harness state (the ChangeProposal store + audit trail).
/// </para>
/// </remarks>
public sealed class GitOpsProposeRemediationTool : ITool
{
    /// <summary>Tool key — matches the keyed-DI registration and the SKILL.md allowed-tools entry.</summary>
    public const string ToolName = "propose_remediation";

    private static readonly IReadOnlyList<string> Operations = ["submit"];
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IGitOpsController _controller;
    private readonly IGitOpsRemediationDispatcher _dispatcher;

    /// <summary>Initialises a new instance of the <see cref="GitOpsProposeRemediationTool"/> class.</summary>
    /// <param name="controller">The active GitOps controller resolved from configuration.</param>
    /// <param name="dispatcher">Wraps a remediation plan into a <c>ChangeProposal</c> and routes it through the gate pipeline.</param>
    public GitOpsProposeRemediationTool(IGitOpsController controller, IGitOpsRemediationDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _controller = controller;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public string Description =>
        "Detects drift, proposes a Git-side remediation, and submits it as a ChangeProposal for gate evaluation and approval. Never mutates the cluster or repo directly. Returns the proposal as JSON.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public BlastRadius RiskTier => BlastRadius.High;

    /// <inheritdoc />
    public bool IsConcurrencySafe => false;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, "submit", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"Unknown operation: {operation}. Supported: submit.");

        var drift = await _controller.DetectDriftAsync(cancellationToken).ConfigureAwait(false);
        if (!drift.IsSuccess)
            return ToolResult.Fail(string.Join("; ", drift.Errors));

        if (drift.Value!.DriftedResources.Count == 0)
            return ToolResult.Ok("No drift detected; no remediation proposal submitted.");

        var plan = await _controller.ProposeRemediationAsync(drift.Value, cancellationToken).ConfigureAwait(false);
        if (!plan.IsSuccess)
            return ToolResult.Fail(string.Join("; ", plan.Errors));

        var dispatched = await _dispatcher.DispatchAsync(plan.Value!, cancellationToken).ConfigureAwait(false);
        if (!dispatched.IsSuccess)
            return ToolResult.Fail(string.Join("; ", dispatched.Errors));

        return ToolResult.Ok(JsonSerializer.Serialize(dispatched.Value, SerializerOptions));
    }
}
