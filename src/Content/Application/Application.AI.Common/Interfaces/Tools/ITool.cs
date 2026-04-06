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
