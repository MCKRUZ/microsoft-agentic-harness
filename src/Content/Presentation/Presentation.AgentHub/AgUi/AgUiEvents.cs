using System.Text.Json.Serialization;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Base type for all AG-UI protocol events serialized as SSE <c>data:</c> frames.
/// <para>
/// The <c>type</c> property serves as the polymorphic discriminator on the wire.
/// Callers must serialize against this base type so that <see cref="JsonPolymorphicAttribute"/>
/// emits the correct discriminator for each derived event. Serializing a derived type
/// directly bypasses polymorphism and omits the discriminator.
/// </para>
/// <para>
/// Derived event records are organized by category across sibling files:
/// <list type="bullet">
///   <item><description><c>AgUiRunEvents.cs</c> — Run lifecycle and text message streaming</description></item>
///   <item><description><c>AgUiPlanEvents.cs</c> — Plan execution, steps, state deltas, sandbox</description></item>
///   <item><description><c>AgUiEscalationEvents.cs</c> — Escalation request/resolve/expiry</description></item>
///   <item><description><c>AgUiDriftEvents.cs</c> — Quality drift detection and resolution</description></item>
///   <item><description><c>AgUiLearningEvents.cs</c> — Learning capture, application, forgetting</description></item>
/// </list>
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
// Run lifecycle
[JsonDerivedType(typeof(RunStartedEvent), AgUiEventType.RunStarted)]
[JsonDerivedType(typeof(RunFinishedEvent), AgUiEventType.RunFinished)]
[JsonDerivedType(typeof(RunErrorEvent), AgUiEventType.RunError)]
[JsonDerivedType(typeof(TextMessageStartEvent), AgUiEventType.TextMessageStart)]
[JsonDerivedType(typeof(TextMessageContentEvent), AgUiEventType.TextMessageContent)]
[JsonDerivedType(typeof(TextMessageEndEvent), AgUiEventType.TextMessageEnd)]
// Escalation
[JsonDerivedType(typeof(EscalationRequestedEvent), AgUiEventType.EscalationRequested)]
[JsonDerivedType(typeof(EscalationResolvedEvent), AgUiEventType.EscalationResolved)]
[JsonDerivedType(typeof(EscalationExpiringEvent), AgUiEventType.EscalationExpiring)]
// Drift detection
[JsonDerivedType(typeof(DriftWarnEvent), AgUiEventType.DriftWarn)]
[JsonDerivedType(typeof(DriftAlertEvent), AgUiEventType.DriftAlert)]
[JsonDerivedType(typeof(DriftEscalateEvent), AgUiEventType.DriftEscalate)]
[JsonDerivedType(typeof(DriftResolvedEvent), AgUiEventType.DriftResolved)]
// Learnings
[JsonDerivedType(typeof(LearningCapturedEvent), AgUiEventType.LearningCaptured)]
[JsonDerivedType(typeof(LearningAppliedEvent), AgUiEventType.LearningApplied)]
[JsonDerivedType(typeof(LearningForgottenEvent), AgUiEventType.LearningForgotten)]
// Plan execution
[JsonDerivedType(typeof(PlanStartedEvent), AgUiEventType.PlanStarted)]
[JsonDerivedType(typeof(PlanStepStartedEvent), AgUiEventType.PlanStepStarted)]
[JsonDerivedType(typeof(PlanStepCompletedEvent), AgUiEventType.PlanStepCompleted)]
[JsonDerivedType(typeof(PlanStateUpdateEvent), AgUiEventType.PlanStateDelta)]
[JsonDerivedType(typeof(SandboxStatusEvent), AgUiEventType.SandboxStatus)]
[JsonDerivedType(typeof(PlanCompletedEvent), AgUiEventType.PlanCompleted)]
[JsonDerivedType(typeof(PlanFailedEvent), AgUiEventType.PlanFailed)]
public abstract record AgUiEvent;
