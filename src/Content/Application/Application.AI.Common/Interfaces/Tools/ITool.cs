using Domain.AI.Changes;
using Domain.AI.Models;

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
