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
/// Register this provider <em>after</em> <see cref="AgentSkillsProvider"/> in the
/// <c>AIContextProviders</c> list so it operates on the fully-built tool set, including any
/// framework tools surfaced by progressive skill disclosure.
/// </para>
/// <para>
/// Enforcement is layered, and this filter owns the outermost layer. <c>ToolChainBuilder</c> already
/// applies the allow-list and the plugin <c>AllowedTools</c>/<c>DeniedTools</c> boundary while the
/// tool chain is being built. Tools the framework injects <em>after</em> that — those surfaced by
/// progressive skill disclosure — never pass through the builder, so this provider is their only
/// enforcement point. Removing it leaves the build-time filtering intact but lets framework-injected
/// tools through unchecked.
/// </para>
/// <para>
/// <b>This filter overrides <see cref="AIContextProvider.InvokingCoreAsync"/>, not
/// <c>ProvideAIContextAsync</c>, and that choice is load-bearing.</b> <c>ProvideAIContextAsync</c> is
/// contractually an <em>additive</em> hook: whatever it returns is merged into the incoming context by
/// the base implementation, which computes <c>Tools = input.Concat(provided)</c>. A subtractive filter
/// implemented there removes nothing — every tool it drops is restored from the input, and every tool it
/// keeps is duplicated. Removal is only possible by overriding the merge itself. Any future refactor that
/// moves this logic back onto <c>ProvideAIContextAsync</c> silently disables the control while leaving
/// its unit tests green, so <c>ToolPermissionFilterTests</c> asserts through the public
/// <see cref="AIContextProvider.InvokingAsync"/> entry point that the runtime actually uses.
/// </para>
/// <para>
/// Two framework tools are exempt from the allow-list — <c>load_skill</c> and
/// <c>read_skill_resource</c> (see <see cref="SkillDisclosureToolNames"/>). They are the transport for
/// skill instructions rather than capabilities an agent is granted, and every skill's manifest
/// allow-list names domain tools only, so filtering them would disable progressive disclosure for
/// precisely those agents that declare tool restrictions. <c>run_skill_script</c> is deliberately
/// <em>not</em> exempt: it is the one skill tool that executes something, so it stays subject to the
/// allow-list. This mirrors the framework's own read-only/all split in
/// <c>AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule</c>.
/// </para>
/// <para>
/// Do not expect the framework to cover that gap: as of <c>Microsoft.Agents.AI</c> 1.13.0,
/// <c>AgentSkillFrontmatter.AllowedTools</c> is written by its parser and read by nothing — it is
/// advisory metadata, not a control.
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

    /// <summary>
    /// The framework skill tools that carry skill <em>content</em> rather than granting a capability, and
    /// are therefore never filtered. Both are read-only. <c>run_skill_script</c> is excluded by design —
    /// see the remarks on <see cref="ToolPermissionFilter"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> SkillDisclosureToolNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AgentSkillsProvider.LoadSkillToolName,
            AgentSkillsProvider.ReadSkillResourceToolName,
        };

    /// <inheritdoc />
    /// <remarks>
    /// Overrides the merge rather than <c>ProvideAIContextAsync</c> because this provider <em>removes</em>
    /// tools, which the additive hook cannot express. See the remarks on <see cref="ToolPermissionFilter"/>.
    /// </remarks>
    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // Let the base assemble the full accumulated context first (instructions, message source
        // stamping, and the tools contributed by every provider ahead of this one), then subtract.
        var merged = await base.InvokingCoreAsync(context, cancellationToken).ConfigureAwait(false);

        // No restriction — pass the merged context through unchanged. An empty (but non-null) allow-list
        // is NOT this case: it is an active restriction that denies every tool.
        var allowed = _allowedTools;
        if (allowed is null)
            return merged;

        var allTools = merged.Tools?.ToList();
        if (allTools is null or { Count: 0 })
            return merged;

        var filtered = allTools
            .Where(t => allowed.Contains(t.Name) || SkillDisclosureToolNames.Contains(t.Name))
            .ToList();

        // Nothing was removed — avoid allocating a new AIContext
        if (filtered.Count == allTools.Count)
            return merged;

        return new AIContext
        {
            Instructions = merged.Instructions,
            Messages = merged.Messages,
            Tools = filtered
        };
    }
}
