using System.Text.Json.Serialization;

namespace Domain.AI.Planner;

/// <summary>
/// Abstract base for step-specific configuration. Each <see cref="StepType"/> has a corresponding
/// concrete subtype. Polymorphic JSON serialization uses the <c>type</c> discriminator property
/// for round-tripping through EF Core JSON columns and API payloads.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmCallConfig), "llm_call")]
[JsonDerivedType(typeof(ToolUseConfig), "tool_use")]
[JsonDerivedType(typeof(HumanGateConfig), "human_gate")]
[JsonDerivedType(typeof(ConditionalBranchConfig), "conditional_branch")]
[JsonDerivedType(typeof(SubPlanConfig), "sub_plan")]
public abstract record StepConfiguration;
