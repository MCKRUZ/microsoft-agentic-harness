namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Configuration for a step that drives model inference with a workflow-authored system prompt.
/// </summary>
/// <remarks>
/// <para>
/// This is the step type that spends money on the host's credentials, so it is authorized as a
/// capability in its own right under a reserved name. A caller whose envelope withholds it can submit
/// a workflow containing inference steps, but every one of them fails closed when run — which is the
/// control that stops an otherwise fully-constrained caller from buying unbounded tokens.
/// </para>
/// <para>
/// <see cref="SystemPrompt"/> is caller-authored text that becomes a system prompt. It is treated as
/// untrusted throughout: it is never concatenated with host instructions during submission, and the
/// model deployment it runs against is chosen from the host's configured deployments by key, never
/// supplied as an endpoint or model name on the wire.
/// </para>
/// </remarks>
public sealed record LlmCallStepConfiguration : WorkflowStepConfiguration
{
    /// <summary>The system prompt for this inference step.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// Key of one of the host's configured model deployments. A key the host does not recognise is a
    /// validation failure — the caller cannot name an arbitrary endpoint or model, only choose among
    /// what the host has already provisioned.
    /// </summary>
    public required string ModelDeploymentKey { get; init; }

    /// <summary>Sampling temperature. When omitted, the host default applies.</summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Maximum tokens for this step's response. Bounded by the host's ceiling; a request above it is
    /// rejected rather than clamped. Note that this bounds one step, not the run — the run-level token
    /// budget is enforced separately and is what stops a many-step workflow from summing its way past
    /// any per-step limit.
    /// </summary>
    public int? MaxTokens { get; init; }
}
