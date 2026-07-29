using Domain.AI.Planner;
namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Configuration for a step that invokes one of the host's registered tools.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Naming a tool here does not grant access to it.</strong> Every invocation is authorized at
/// run time against the caller's capability envelope through the same governor that gates an agent's
/// own tool calls. A workflow may name a tool the caller was never granted; that step fails closed
/// when it runs. Submission validates shape, not entitlement — the two are deliberately separate,
/// because a caller's grants can change between submission and execution.
/// </para>
/// <para>
/// The domain type's <c>IsolationLevelOverride</c> is absent here by design. It lets the step request
/// a weaker sandbox than the host would otherwise use, which is a decision the host must own — a
/// submitter asking to be less contained is exactly the request that should not be honoured.
/// </para>
/// </remarks>
public sealed record ToolUseStepConfiguration : WorkflowStepConfiguration
{
    /// <inheritdoc />
    public override StepType StepType => StepType.ToolUse;

    /// <summary>
    /// The registered name of the tool to invoke, matched case-insensitively — the same comparison the
    /// envelope allowlist uses, so a name that resolves here resolves identically when authorized.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Arguments passed to the tool. Values are treated as untrusted data and are never interpolated
    /// into a prompt or a command line by the submission path.
    /// </summary>
    public IReadOnlyDictionary<string, object?> InputParameters { get; init; } =
        new Dictionary<string, object?>();
}
