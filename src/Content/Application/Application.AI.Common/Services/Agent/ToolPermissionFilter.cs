using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// An <see cref="AIContextProvider"/> that enforces a per-skill allowed-tools constraint on
/// every agent invocation. Any tool not in the allow-list is stripped from the accumulated
/// <see cref="AIContext"/> before the model is called, ensuring an agent can only see —
/// and invoke — the tools its skill declaration explicitly permits.
/// </summary>
/// <remarks>
/// <para>
/// Register this provider <em>after</em> <see cref="FileAgentSkillsProvider"/> in the
/// <c>AIContextProviders</c> list so it operates on the fully-built tool set, including any
/// framework tools surfaced by progressive skill disclosure.
/// </para>
/// <para>
/// When the allow-list is empty the filter is a no-op: all tools pass through unchanged.
/// Tool name comparison is case-insensitive.
/// </para>
/// </remarks>
public class ToolPermissionFilter : AIContextProvider
{
    private readonly IReadOnlySet<string> _allowedTools;

    /// <summary>
    /// Initializes a new <see cref="ToolPermissionFilter"/> that restricts invocations to
    /// the specified tool names.
    /// </summary>
    /// <param name="allowedTools">
    /// The set of tool names the agent may use. An empty collection means no restriction.
    /// </param>
    public ToolPermissionFilter(IEnumerable<string> allowedTools)
        : base(
            provideInputMessageFilter: messages => messages,
            storeInputRequestMessageFilter: messages => messages,
            storeInputResponseMessageFilter: messages => messages)
    {
        _allowedTools = new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // No restriction when allow-list is empty — pass context through unchanged
        if (_allowedTools.Count == 0)
            return ValueTask.FromResult(context.AIContext);

        var allTools = context.AIContext.Tools?.ToList();
        if (allTools is null or { Count: 0 })
            return ValueTask.FromResult(context.AIContext);

        var filtered = allTools
            .Where(t => _allowedTools.Contains(t.Name))
            .ToList();

        // Nothing was removed — avoid allocating a new AIContext
        if (filtered.Count == allTools.Count)
            return ValueTask.FromResult(context.AIContext);

        return ValueTask.FromResult(new AIContext
        {
            Instructions = context.AIContext.Instructions,
            Messages = context.AIContext.Messages,
            Tools = filtered
        });
    }
}
