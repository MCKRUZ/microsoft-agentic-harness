using Domain.AI.Changes;
using Domain.AI.Models;
using Domain.Common.Config.AI.Governance;

namespace Application.AI.Common.Interfaces.Tools;

/// <summary>
/// Framework-independent contract for a tool that can be invoked by an AI agent.
/// Tools are registered via keyed DI and resolved by name when a skill declares them.
/// </summary>
/// <remarks>
/// <para>
/// This interface is the harness's abstraction over tools. The LLM never sees <c>ITool</c>
/// directly — an <see cref="IToolConverter"/> bridges it to <c>Microsoft.Extensions.AI.AITool</c>
/// for the chat pipeline. This separation keeps tool implementations framework-independent
/// and testable without AI SDK dependencies.
/// </para>
/// <para>
/// <strong>Tool lifecycle:</strong>
/// <list type="number">
///   <item>SKILL.md declares <c>tools: [{name: "file_system", operations: [read, write]}]</c></item>
///   <item>Harness resolves <c>"file_system"</c> from keyed DI as <c>ITool</c></item>
///   <item><see cref="IToolConverter"/> converts the tool to <c>AIFunction</c> (with auto-generated JSON Schema)</item>
///   <item><c>AIFunction</c> goes into <c>ChatOptions.Tools</c> — the LLM sees the schema</item>
///   <item>Framework's <c>UseFunctionInvocation</c> middleware dispatches calls automatically</item>
/// </list>
/// </para>
/// <para>
/// <strong>Registration pattern:</strong>
/// <code>
/// services.AddKeyedSingleton&lt;ITool&gt;("file_system", (sp, key) =&gt; new FileSystemTool(...));
/// </code>
/// </para>
/// <para>
/// <strong>Concurrency classification:</strong>
/// Tools declare their concurrency safety via <see cref="IsReadOnly"/> and <see cref="IsConcurrencySafe"/>.
/// The <see cref="IToolConcurrencyClassifier"/> uses these properties to partition batched tool calls
/// into parallel (read-only) and serial (write) groups. Default values are fail-closed (assumes writes).
/// </para>
/// </remarks>
public interface ITool
{
    /// <summary>Gets the unique tool name matching the keyed DI registration and SKILL.md declaration.</summary>
    string Name { get; }

    /// <summary>Gets a human-readable description of what the tool does, used for LLM tool schema generation.</summary>
    string Description { get; }

    /// <summary>Gets the list of operations this tool supports (e.g., "read", "write", "list").</summary>
    IReadOnlyList<string> SupportedOperations { get; }

    /// <summary>
    /// Whether this tool only reads state and never modifies it.
    /// Read-only tools can safely run in parallel during batched execution.
    /// Default is false (fail-closed — assumes writes).
    /// </summary>
    bool IsReadOnly => false;

    /// <summary>
    /// Whether this tool is safe to run concurrently with other tool invocations.
    /// Default is false (fail-closed — assumes not safe).
    /// </summary>
    bool IsConcurrencySafe => false;

    /// <summary>
    /// Whether this tool means anything when a caller invokes it directly over HTTP, rather than an
    /// agent invoking it mid-turn. Default is true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is a transport-suitability fact, not an authorization control.</strong> What a
    /// caller may invoke is decided by their <c>CapabilityEnvelope</c> and enforced by
    /// <c>IToolInvocationGovernor</c>; nothing here widens or narrows that. This answers a different
    /// question the envelope cannot: whether the tool's result is even coherent once it leaves the
    /// process. Setting it false removes the tool from the direct-invocation surface only — the tool
    /// remains fully available to agents, plans, and skills.
    /// </para>
    /// <para>
    /// <strong>Two shapes of tool should set this false.</strong> The first returns a directive rather
    /// than an answer: the <c>render_*</c> family and <c>dashboard_control</c> emit instructions for a
    /// connected AG-UI client to act on, so an HTTP caller receives a reference to a widget it has no
    /// session with. The second turns one call into unbounded work: <c>delegate_task</c> spawns agent
    /// turns, so a caller sees a synchronous tool call time out while the host keeps spending on
    /// inference behind it.
    /// </para>
    /// <para>
    /// <strong>Why the default is true rather than fail-closed</strong>, unlike its neighbours above.
    /// Those two default false because guessing wrong about them causes damage — a tool wrongly assumed
    /// read-only gets run in parallel and corrupts state. Guessing wrong here costs a caller a
    /// confusing response, and the envelope still had to name the tool for them to reach it at all.
    /// Defaulting false would instead mean a consumer's own tools are silently absent from a surface
    /// they explicitly granted, which is the harder failure to diagnose: nothing is wrong, and nothing
    /// says why.
    /// </para>
    /// </remarks>
    bool IsDirectlyInvocable => true;

    /// <summary>
    /// The intrinsic blast radius (impact band) of invoking this tool — how much damage
    /// a single call can do. Feeds the graded-autonomy engine: higher tiers may
    /// auto-approve low-radius tools while still requiring human approval for high-radius
    /// ones, and the escalation severity is derived from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default is <see cref="BlastRadius.Medium"/> — a neutral middle that preserves the
    /// harness's prior fixed risk treatment for tools that do not classify themselves.
    /// Tools should override this to declare their true impact: read-only lookups as
    /// <see cref="BlastRadius.Trivial"/>/<see cref="BlastRadius.Low"/>; operations that
    /// touch production state, run commands, or apply infrastructure as
    /// <see cref="BlastRadius.High"/>/<see cref="BlastRadius.Critical"/>.
    /// </para>
    /// <para>
    /// This is the tool-level (worst-case) rating across all <see cref="SupportedOperations"/>;
    /// per-operation risk refinement is a separate concern. The reused
    /// <see cref="BlastRadius"/> scale lets a tool's rating flow directly into the same
    /// graded-autonomy evaluator that governs change proposals, with no mapping layer.
    /// </para>
    /// </remarks>
    BlastRadius RiskTier => BlastRadius.Medium;

    /// <summary>
    /// Declares the expected output content type for compression strategy selection.
    /// When null, <c>ContentTypeDetector</c> sniffs the output at runtime.
    /// </summary>
    Domain.AI.Compression.Enums.ToolOutputCategory? OutputCategory => null;

    /// <summary>
    /// Per-tool compression token threshold override. When null, falls back
    /// to <c>ToolOutputCompressionConfig.DefaultTokenThreshold</c>.
    /// </summary>
    int? CompressionTokenThreshold => null;

    /// <summary>
    /// What this tool can do with the data that flows through it — bringing untrusted or sensitive
    /// content into the conversation, or acting on that content in a costly way. Feeds the tool
    /// composition check (<c>IToolCompositionAnalyzer</c>), which flags an agent holding both a source
    /// and a sink capability across its assembled tool set as an indirect-prompt-injection exfiltration
    /// primitive — see <see cref="ToolCompositionCapability"/>.
    /// </summary>
    /// <remarks>
    /// Default is <see cref="ToolCompositionCapability.None"/> — unlike
    /// <see cref="IsReadOnly"/> and <see cref="IsConcurrencySafe"/>, this default is NOT fail-closed.
    /// An unclassified tool contributes no capability bits at all, deliberately, because the
    /// composition check treats "unknown" as neither a source nor a sink: a fail-closed default here
    /// (unknown means both) would flag every agent holding two or more undeclared tools, which is the
    /// "universal taint destroys signal" failure the check exists to avoid. Declare this only where the
    /// answer is unambiguous; an ambiguous tool is better left undeclared than guessed at, and the
    /// composition analyzer's unclassified-tool count makes that gap visible rather than silent.
    /// </remarks>
    ToolCompositionCapability Capabilities => ToolCompositionCapability.None;

    /// <summary>
    /// Executes a tool operation with the given parameters.
    /// </summary>
    /// <param name="operation">The operation to perform (must be in <see cref="SupportedOperations"/>).</param>
    /// <param name="parameters">The operation parameters as key-value pairs, deserialized from the LLM's JSON arguments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ToolResult"/> indicating success with output or failure with error.</returns>
    Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default);
}
