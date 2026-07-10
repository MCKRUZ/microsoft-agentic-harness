using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// An <see cref="AIContextProvider"/> that enforces an allowed-tools constraint on every agent
/// invocation. Any tool not in the allow-list is stripped from the accumulated <see cref="AIContext"/>
/// before the model is called, ensuring an agent can only see — and invoke — the tools it is permitted.
/// </summary>
/// <remarks>
/// <para>
/// Register this provider <em>after</em> <see cref="FileAgentSkillsProvider"/> in the
/// <c>AIContextProviders</c> list so it operates on the fully-built tool set, including any
/// framework tools surfaced by progressive skill disclosure.
/// </para>
/// <para>
/// The allow-list distinguishes two states that a bare empty collection cannot. A <see langword="null"/>
/// allow-list means <em>no restriction</em> — every tool passes through; callers that want no filter
/// should simply not register this provider (or pass null). A non-null allow-list — <em>including an
/// empty one</em> — is an active restriction: only the listed tools survive, and an empty list therefore
/// denies every tool. This is what lets an agent tool ceiling that is disjoint from the skills' tools
/// leave the agent with no tools rather than accidentally granting all of them. Tool name comparison
/// is case-insensitive.
/// </para>
/// </remarks>
public class ToolPermissionFilter : AIContextProvider
{
    private readonly IReadOnlySet<string>? _allowedTools;

    /// <summary>
    /// Initializes a new <see cref="ToolPermissionFilter"/> that restricts invocations to
    /// the specified tool names.
    /// </summary>
    /// <param name="allowedTools">
    /// The set of tool names the agent may use. <see langword="null"/> means no restriction (every
    /// tool passes through). A non-null collection is an active restriction — only these tools survive,
    /// and an empty collection denies every tool.
    /// </param>
    public ToolPermissionFilter(IEnumerable<string>? allowedTools)
        : base(
            provideInputMessageFilter: messages => messages,
            storeInputRequestMessageFilter: messages => messages,
            storeInputResponseMessageFilter: messages => messages)
    {
        _allowedTools = allowedTools is null
            ? null
            : new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The set of tool names this filter permits, or <see langword="null"/> when the filter imposes no
    /// restriction. A non-null (possibly empty) set is an active restriction. Exposed read-only so
    /// callers and tests can observe the effective allowlist wired onto an agent (for example, the
    /// agent tool ceiling intersected with its skills' allowlists).
    /// </summary>
    public IReadOnlySet<string>? AllowedTools => _allowedTools;

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // No restriction — pass context through unchanged. An empty (but non-null) allow-list is NOT
        // this case: it is an active restriction that denies every tool.
        if (_allowedTools is null)
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
