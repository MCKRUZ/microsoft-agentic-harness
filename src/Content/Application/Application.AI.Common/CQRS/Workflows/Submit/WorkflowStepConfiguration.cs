using System.Text.Json.Serialization;

namespace Application.AI.Common.CQRS.Workflows.Submit;

/// <summary>
/// Abstract base for the step-specific configuration a caller may submit over HTTP. Each
/// <see cref="Domain.AI.Planner.StepType"/> that is accepted on the wire has a corresponding
/// concrete subtype here.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is deliberately not <c>Domain.AI.Planner.StepConfiguration</c>.</strong> That type
/// carries the same <c>type</c> discriminator and would deserialize a submission directly — which is
/// precisely why it must not be used. It is the *storage* format: <c>EfCorePlanStateStore</c> writes
/// it to the <c>ConfigurationJson</c> column with the same serializer options. Binding the public
/// contract to it would weld the wire format to an EF JSON column, so a field rename would break every
/// caller and every stored row in one change.
/// </para>
/// <para>
/// Three members of the domain type are also unsafe to accept from an external caller, and are absent
/// here rather than merely ignored — an absent property cannot be reinstated by a future edit that
/// "tidies up" a mapper:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>ToolUseConfig.IsolationLevelOverride</c> — a caller-supplied knob that *weakens* the sandbox
///     the step runs in. The host decides isolation, not the submitter.
///   </description></item>
///   <item><description>
///     <c>RetrievalStepConfiguration.CollectionName</c> — names a retrieval collection directly, which
///     is the cross-tenant read primitive the scoped-collections work exists to prevent. The collection
///     is derived server-side from the caller's scope.
///   </description></item>
///   <item><description>
///     <c>SubPlanConfig.InlinePlanDefinition</c> — an entire nested plan in the request body, making the
///     payload recursive and the nesting-depth cap a parser concern. Accepted only by reference; see
///     <see cref="SubPlanStepConfiguration"/>.
///   </description></item>
/// </list>
/// <para>
/// The precedent for a separate wire shape mapped into the domain model already exists in this
/// codebase: <c>LlmPlanOutput</c> plus <c>LlmPlanOutputMapper</c> do exactly this for LLM-authored
/// plans. This is that pattern's sibling for caller-authored plans, not a new idea.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmCallStepConfiguration), "llm_call")]
[JsonDerivedType(typeof(ToolUseStepConfiguration), "tool_use")]
[JsonDerivedType(typeof(HumanGateStepConfiguration), "human_gate")]
[JsonDerivedType(typeof(ConditionalBranchStepConfiguration), "conditional_branch")]
[JsonDerivedType(typeof(SubPlanStepConfiguration), "sub_plan")]
[JsonDerivedType(typeof(RetrievalWorkflowStepConfiguration), "retrieval")]
public abstract record WorkflowStepConfiguration
{
    /// <summary>
    /// The step type this configuration belongs to, so a submission's <see cref="WorkflowStep.Type"/>
    /// can be checked against the body it actually carries.
    /// </summary>
    /// <remarks>
    /// Derived from the concrete type rather than deserialized, and marked <see cref="JsonIgnoreAttribute"/>
    /// so it is never read from or written to the wire — the JSON <c>type</c> discriminator is the only
    /// thing a caller supplies. Without this, the two statements of a step's kind (its <c>Type</c>
    /// property and its configuration's discriminator) could disagree with nothing able to notice.
    /// </remarks>
    [JsonIgnore]
    public abstract Domain.AI.Planner.StepType StepType { get; }
}
