namespace Domain.AI.Telemetry.Conventions;

/// <summary>
/// Single registry of OpenTelemetry GenAI semantic convention attribute and
/// metric names used by the harness, plus harness-vendored extensions under
/// the <c>gen_ai.harness.*</c> namespace.
/// </summary>
/// <remarks>
/// <para>
/// This file is the authoritative entry point for the OTel <c>gen_ai.*</c>
/// vocabulary. New code MUST reference constants from here (or from the
/// per-domain conventions files it re-exports) — never inline a <c>gen_ai.*</c>
/// string literal anywhere else.
/// </para>
/// <para>
/// To prevent duplication, keys that already live in
/// <see cref="AgentConventions"/>, <see cref="ToolConventions"/>, or
/// <see cref="TokenConventions"/> are re-exported here as references, not
/// redeclared. Keys that the harness did not previously define are declared
/// here for the first time.
/// </para>
/// <para>
/// Tracking spec: OpenTelemetry Semantic Conventions for GenAI
/// (Experimental as of 2026-06; see
/// <c>https://opentelemetry.io/docs/specs/semconv/gen-ai/</c>).
/// Harness-vendored keys sit under <c>gen_ai.harness.*</c> to avoid colliding
/// with any future reserved key in the official spec.
/// </para>
/// </remarks>
public static class GenAiSemconvRegistry
{
    // ─────────────────────────────────────────────────────────────────────
    // System / provider identity
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Provider identifier the request was dispatched to (effective provider,
    /// after Polly fallback resolution). Re-exported from
    /// <see cref="AgentConventions.GenAiSystem"/>.
    /// Standard values include <c>azure.ai.openai</c>, <c>anthropic</c>,
    /// <c>openai</c>, <c>az.ai.inference</c>, <c>vertex_ai</c>,
    /// <c>aws.bedrock</c>; the harness also defines
    /// <see cref="AgentConventions.GenAiSystemSemanticKernel"/>,
    /// <see cref="AgentConventions.GenAiSystemExtensionsAI"/>, and
    /// <see cref="AgentConventions.GenAiSystemAgentsAI"/>.
    /// </summary>
    public const string System = AgentConventions.GenAiSystem;

    /// <summary>
    /// Provider that the call was originally intended for, before any
    /// fallback occurred. Set only when it differs from <see cref="System"/>.
    /// Harness-vendored.
    /// </summary>
    public const string SystemIntended = "gen_ai.harness.system.intended";

    // ─────────────────────────────────────────────────────────────────────
    // Operation
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Operation kind. Re-exported from <see cref="ToolConventions.GenAiOperationName"/>.
    /// Standard values: <c>chat</c>, <c>text_completion</c>, <c>embeddings</c>,
    /// <c>execute_tool</c>, <c>generate_content</c>, <c>invoke_agent</c>.
    /// </summary>
    public const string OperationName = ToolConventions.GenAiOperationName;

    /// <summary>
    /// Operation value: chat completion. Re-exported from
    /// <see cref="ToolConventions.ChatOperation"/>.
    /// </summary>
    public const string OperationChat = ToolConventions.ChatOperation;

    /// <summary>
    /// Operation value: text completion (legacy). Re-exported from
    /// <see cref="ToolConventions.TextCompletionOperation"/>.
    /// </summary>
    public const string OperationTextCompletion = ToolConventions.TextCompletionOperation;

    /// <summary>
    /// Operation value: embeddings. Re-exported from
    /// <see cref="ToolConventions.EmbeddingsOperation"/>.
    /// </summary>
    public const string OperationEmbeddings = ToolConventions.EmbeddingsOperation;

    /// <summary>
    /// Operation value: tool execution span. Re-exported from
    /// <see cref="ToolConventions.ExecuteToolOperation"/>.
    /// </summary>
    public const string OperationExecuteTool = ToolConventions.ExecuteToolOperation;

    /// <summary>
    /// Operation value: agent invocation (top-level harness turn). Re-exported from
    /// <see cref="ToolConventions.InvokeAgentOperation"/>.
    /// </summary>
    public const string OperationInvokeAgent = ToolConventions.InvokeAgentOperation;

    // ─────────────────────────────────────────────────────────────────────
    // Request
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Requested model identifier. Re-exported from
    /// <see cref="TokenConventions.GenAiRequestModel"/>.
    /// </summary>
    public const string RequestModel = TokenConventions.GenAiRequestModel;

    /// <summary>Maximum tokens requested.</summary>
    public const string RequestMaxTokens = "gen_ai.request.max_tokens";

    /// <summary>Sampling temperature requested.</summary>
    public const string RequestTemperature = "gen_ai.request.temperature";

    /// <summary>Top-p value requested.</summary>
    public const string RequestTopP = "gen_ai.request.top_p";

    /// <summary>Top-k value requested.</summary>
    public const string RequestTopK = "gen_ai.request.top_k";

    /// <summary>Stop sequences for the request.</summary>
    public const string RequestStopSequences = "gen_ai.request.stop_sequences";

    // ─────────────────────────────────────────────────────────────────────
    // Response
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Provider response id.</summary>
    public const string ResponseId = "gen_ai.response.id";

    /// <summary>Model id reported in the response (post-routing).</summary>
    public const string ResponseModel = "gen_ai.response.model";

    /// <summary>Array of finish reasons.</summary>
    public const string ResponseFinishReasons = "gen_ai.response.finish_reasons";

    // ─────────────────────────────────────────────────────────────────────
    // Token usage (re-exported)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Re-exported from <see cref="TokenConventions.GenAiInputTokens"/>.</summary>
    public const string UsageInputTokens = TokenConventions.GenAiInputTokens;

    /// <summary>Re-exported from <see cref="TokenConventions.GenAiOutputTokens"/>.</summary>
    public const string UsageOutputTokens = TokenConventions.GenAiOutputTokens;

    /// <summary>Re-exported from <see cref="TokenConventions.GenAiCacheReadTokens"/>.</summary>
    public const string UsageCacheReadInputTokens = TokenConventions.GenAiCacheReadTokens;

    /// <summary>Re-exported from <see cref="TokenConventions.GenAiCacheWriteTokens"/>.</summary>
    public const string UsageCacheCreationInputTokens = TokenConventions.GenAiCacheWriteTokens;

    // ─────────────────────────────────────────────────────────────────────
    // Agent (multi-skill harness identity)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Agent display name.</summary>
    public const string AgentName = "gen_ai.agent.name";

    /// <summary>Stable agent id.</summary>
    public const string AgentId = "gen_ai.agent.id";

    /// <summary>Agent description.</summary>
    public const string AgentDescription = "gen_ai.agent.description";

    /// <summary>
    /// Conversation correlation id under the OTel <c>gen_ai.*</c> namespace.
    /// </summary>
    /// <remarks>
    /// During the migration to <c>gen_ai.*</c>, both this key AND the legacy
    /// <see cref="LegacyConversationId"/> MUST be emitted on every agent span.
    /// The pair is co-located here so the parallel-emit site is impossible to
    /// half-update. When the migration completes,
    /// <see cref="LegacyConversationId"/> is removed first, then
    /// <see cref="AgentConventions.ConversationId"/>.
    /// </remarks>
    public const string ConversationId = "gen_ai.conversation.id";

    /// <summary>
    /// Legacy conversation correlation id. Re-exported from
    /// <see cref="AgentConventions.ConversationId"/>. See remarks on
    /// <see cref="ConversationId"/> for the parallel-emit contract.
    /// </summary>
    public const string LegacyConversationId = AgentConventions.ConversationId;

    // ─────────────────────────────────────────────────────────────────────
    // Errors
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Error type attribute used by gen_ai spans. Per OTel SemConv this is
    /// the canonical <c>error.type</c> attribute (no <c>gen_ai.</c> prefix),
    /// shared across all signals. Tail sampling at the Collector pivots on
    /// the presence of this attribute (see blueprint G6).
    /// </summary>
    public const string ErrorType = "error.type";

    // ─────────────────────────────────────────────────────────────────────
    // Tools (re-exported, with new keys for parts SemConv adds)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Re-exported from <see cref="ToolConventions.GenAiToolName"/>.</summary>
    public const string ToolName = ToolConventions.GenAiToolName;

    /// <summary>Re-exported from <see cref="ToolConventions.GenAiToolCallId"/>.</summary>
    public const string ToolCallId = ToolConventions.GenAiToolCallId;

    /// <summary>Re-exported from <see cref="ToolConventions.GenAiToolType"/>.</summary>
    public const string ToolType = ToolConventions.GenAiToolType;

    /// <summary>Re-exported from <see cref="ToolConventions.GenAiToolDescription"/>.</summary>
    public const string ToolDescription = ToolConventions.GenAiToolDescription;

    /// <summary>Re-exported from <see cref="ToolConventions.ToolCallArguments"/>.</summary>
    public const string ToolCallArguments = ToolConventions.ToolCallArguments;

    /// <summary>Re-exported from <see cref="ToolConventions.ToolCallResult"/>.</summary>
    public const string ToolCallResult = ToolConventions.ToolCallResult;

    // ─────────────────────────────────────────────────────────────────────
    // Output
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Output content type. Standard values: <c>text</c>, <c>json</c>,
    /// <c>image</c>, <c>speech</c>.
    /// </summary>
    public const string OutputType = "gen_ai.output.type";

    // ─────────────────────────────────────────────────────────────────────
    // Harness-vendored extensions (under gen_ai.harness.*)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Skill currently executing within a multi-skill compose. Harness-vendored.</summary>
    public const string HarnessSkillName = "gen_ai.harness.skill.name";

    /// <summary>Skill mode: <c>managed</c> or <c>injected</c>. Harness-vendored.</summary>
    public const string HarnessSkillMode = "gen_ai.harness.skill.mode";

    /// <summary>Plugin id that contributed the active skill, if any. Harness-vendored.</summary>
    public const string HarnessPluginId = "gen_ai.harness.plugin.id";

    /// <summary>
    /// Re-exported from <see cref="ToolConventions.HarnessCandidateId"/>. Harness-vendored.
    /// </summary>
    public const string HarnessCandidateId = ToolConventions.HarnessCandidateId;

    /// <summary>
    /// Re-exported from <see cref="ToolConventions.HarnessIteration"/>. Harness-vendored.
    /// </summary>
    public const string HarnessIteration = ToolConventions.HarnessIteration;

    // ────────────────────────────────────────────────────────────────────
    // Magentic orchestration (re-exported from MagenticConventions)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Operation value used on the root Magentic workflow span.
    /// Re-exported from <see cref="MagenticConventions.OperationInvokeWorkflow"/>.
    /// </summary>
    public const string OperationInvokeWorkflow = MagenticConventions.OperationInvokeWorkflow;

    /// <summary>
    /// Workflow name attribute on the root Magentic workflow span.
    /// Re-exported from <see cref="MagenticConventions.WorkflowName"/>.
    /// </summary>
    public const string WorkflowName = MagenticConventions.WorkflowName;

    /// <summary>
    /// Magentic orchestration role discriminator (<c>manager</c> | <c>participant</c>).
    /// Re-exported from <see cref="MagenticConventions.Role"/>.
    /// </summary>
    public const string MagenticRole = MagenticConventions.Role;

    /// <summary>
    /// Monotonic Magentic plan version on the manager span.
    /// Re-exported from <see cref="MagenticConventions.PlanVersion"/>.
    /// </summary>
    public const string MagenticPlanVersion = MagenticConventions.PlanVersion;

    /// <summary>
    /// Magentic round number on the per-round span.
    /// Re-exported from <see cref="MagenticConventions.RoundNumber"/>.
    /// </summary>
    public const string MagenticRoundNumber = MagenticConventions.RoundNumber;

    /// <summary>
    /// Magentic reset number on the reset span.
    /// Re-exported from <see cref="MagenticConventions.ResetNumber"/>.
    /// </summary>
    public const string MagenticResetNumber = MagenticConventions.ResetNumber;

    /// <summary>
    /// Name of the OTel <c>ActivitySource</c> for Magentic orchestration spans.
    /// Re-exported from <see cref="MagenticConventions.ActivitySourceName"/>.
    /// </summary>
    public const string MagenticActivitySourceName = MagenticConventions.ActivitySourceName;

    // ────────────────────────────────────────────────────────────────────
    // A2A — Agent-to-Agent (re-exported from A2AConventions)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Operation value used on both client and server A2A spans.
    /// Re-exported from <see cref="A2AConventions.OperationInvokeA2A"/>.
    /// </summary>
    public const string OperationInvokeA2A = A2AConventions.OperationInvokeA2A;

    /// <summary>
    /// Caller agent identifier on A2A spans.
    /// Re-exported from <see cref="A2AConventions.CallerId"/>.
    /// </summary>
    public const string A2ACallerId = A2AConventions.CallerId;

    /// <summary>
    /// Callee agent identifier on A2A spans.
    /// Re-exported from <see cref="A2AConventions.CalleeId"/>.
    /// </summary>
    public const string A2ACalleeId = A2AConventions.CalleeId;

    /// <summary>
    /// Correlation id shared by caller and callee A2A spans.
    /// Re-exported from <see cref="A2AConventions.CorrelationId"/>.
    /// </summary>
    public const string A2ACorrelationId = A2AConventions.CorrelationId;

    /// <summary>
    /// Name of the OTel <c>ActivitySource</c> for A2A client/server spans.
    /// Re-exported from <see cref="A2AConventions.ActivitySourceName"/>.
    /// </summary>
    public const string A2AActivitySourceName = A2AConventions.ActivitySourceName;
}
