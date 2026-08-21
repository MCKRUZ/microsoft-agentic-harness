using Application.AI.Common.Services.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// An <see cref="AIContextProvider"/> that closes the <see cref="AIContext"/> tool channel: it drops
/// tools colliding with a reserved plan-capability name and wraps the rest in a
/// <see cref="GovernedAIFunction"/> at invocation time, so both checks run even for tools that never
/// pass through <see cref="ToolChainBuilder"/> — notably framework tools surfaced by progressive skill
/// disclosure, and any tool a consumer's own <see cref="AIContextProvider"/> contributes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Known limitation: admission is governed here, MCP failure detection is not.</strong> This
/// channel carries no equivalent of <c>ToolChainBuilder.ProvisionedTool.McpServerName</c> — a
/// tool's provenance is not tracked once it reaches <c>AIContext.Tools</c> — so <see cref="Govern"/>
/// never wraps a tool in <see cref="McpFailureNormalizingAIFunction"/> the way
/// <see cref="ToolChainBuilder.WrapGoverned"/> does. A consumer whose own <see cref="AIContextProvider"/>
/// contributes an MCP-backed <see cref="AIFunction"/> onto this channel gets admission enforcement, but
/// that tool's non-throwing MCP failure is never normalized to <c>ConvertedToolFailure</c> and is
/// reported <c>Succeeded</c> — the consumer must wrap such a tool in
/// <see cref="McpFailureNormalizingAIFunction"/> themselves before contributing it here. No production
/// path in this harness does this today (confirmed: <c>IMcpToolProvider</c> is only consumed from
/// <see cref="ToolChainBuilder"/>), so this is a documented gap rather than a live one — but it is a
/// template other consumers extend, so it is written down rather than left implicit.
/// </para>
/// <para>
/// Register this provider <em>last</em> in the <c>AIContextProviders</c> list (after the skills
/// provider and <see cref="ToolPermissionFilter"/>) so it sees the final, filtered tool set.
/// Already-wrapped functions and non-function tools pass through unchanged, so it composes safely
/// with the build-time wrapping in <see cref="ToolChainBuilder"/> (no double-wrapping).
/// </para>
/// <para>
/// <strong>Two channels, one filter.</strong> The framework merges <c>ChatOptions.Tools</c> (built by
/// <see cref="ToolChainBuilder"/>) with <c>AIContext.Tools</c> contributed by providers. Only the first
/// passes through the builder, so the reserved plan-capability check is applied here through the shared
/// <see cref="ReservedPlanCapabilityFilter"/> rather than being duplicated — a name the plan engine owns
/// cannot reach the model down either route, and the two enforcement points cannot drift.
/// </para>
/// <para>
/// The governance wrapper is inert unless an admission chain is ambient for the turn (see
/// <see cref="Governance.ToolAdmissionAccessor"/>), so wrapping only adds enforcement. The reserved-name
/// drop is the one case where this provider changes which tools exist, and only for names that must never
/// have been publishable in the first place.
/// </para>
/// <para>
/// <strong>That inertness is what licenses attaching this provider unconditionally, and it must stay
/// unconditional (issue #347).</strong> The rail is assembled once, when the agent is constructed, and
/// agents are cached — but a bundle run arms enforcement afterwards and per run, by publishing a
/// capability envelope (see <see cref="Governance.GovernanceEnforcement.IsActive"/>). A guard at
/// construction therefore cannot see the flow that most needs governing: the earlier one read the host's
/// global switch alone, and on the default composition a bundle's progressive-disclosure tools — two of
/// which are exempt from <see cref="ToolPermissionFilter"/> by design, leaving this their only gate —
/// reached the model unwrapped. Re-adding any condition here reopens that hole for a saving of one list
/// entry on an ungoverned host.
/// </para>
/// <para>
/// <b>This provider overrides <see cref="AIContextProvider.InvokingCoreAsync"/>, not
/// <c>ProvideAIContextAsync</c>, and that choice is load-bearing.</b> <c>ProvideAIContextAsync</c> is
/// contractually an <em>additive</em> hook: the base implementation merges whatever it returns into the
/// incoming context as <c>Tools = input.Concat(provided)</c>. Implemented there, this provider was inert
/// in both of its jobs — every reserved name it dropped was restored from the input, and every tool it
/// wrapped was published <em>alongside</em> its own unwrapped original, leaving the model an ungoverned
/// copy to call. Only overriding the merge can remove or replace a tool. Any future refactor that moves
/// this logic back onto <c>ProvideAIContextAsync</c> silently disables a security control, so
/// <c>AIContextProviderMergeContractTests</c> drives every provider in this assembly through the public
/// <see cref="AIContextProvider.InvokingAsync"/> entry point the runtime actually uses.
/// </para>
/// </remarks>
public sealed class GoverningToolContextProvider : AIContextProvider
{
    /// <summary>Describes this channel on the reserved plan-capability drop log.</summary>
    private const string ChannelDescription = "AIContext.Tools contributed by an AIContextProvider";

    private readonly ILogger<GoverningToolContextProvider> _logger;

    /// <summary>Initializes a new <see cref="GoverningToolContextProvider"/>.</summary>
    /// <param name="logger">Logger that receives reserved plan-capability collision reports.</param>
    public GoverningToolContextProvider(ILogger<GoverningToolContextProvider> logger)
        : base(
            provideInputMessageFilter: messages => messages,
            storeInputRequestMessageFilter: messages => messages,
            storeInputResponseMessageFilter: messages => messages)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Overrides the merge rather than <c>ProvideAIContextAsync</c> because this provider both
    /// <em>removes</em> tools (reserved plan-capability names) and <em>replaces</em> them (governance
    /// wrapping), neither of which the additive hook can express. See the remarks on
    /// <see cref="GoverningToolContextProvider"/>.
    /// </remarks>
    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // Let the base assemble the full accumulated context first — including the tools contributed by
        // every provider ahead of this one — then filter and wrap what it produced.
        var merged = await base.InvokingCoreAsync(context, cancellationToken).ConfigureAwait(false);

        var tools = FilterAndGovern(merged.Tools, _logger);

        // Nothing was dropped or needed wrapping — avoid allocating a new AIContext.
        if (tools is null)
            return merged;

        return new AIContext
        {
            Instructions = merged.Instructions,
            Messages = merged.Messages,
            Tools = tools
        };
    }

    /// <summary>
    /// Applies both checks to one context's tool list: drops reserved plan-capability names, then wraps
    /// the survivors for governance. Returns <see langword="null"/> when the list needs no change, so the
    /// caller can keep the existing <see cref="AIContext"/> instead of allocating an identical one.
    /// Extracted for unit testing of the combined filter.
    /// </summary>
    /// <param name="tools">The tools accumulated on the context, possibly null or empty.</param>
    /// <param name="logger">Logger that receives reserved plan-capability collision reports.</param>
    internal static List<AITool>? FilterAndGovern(IEnumerable<AITool>? tools, ILogger logger)
    {
        var original = tools?.ToList();
        if (original is null or { Count: 0 })
            return null;

        // Reserved-name drop first: a colliding tool must never be published, wrapped or not.
        var permitted = ReservedPlanCapabilityFilter.Exclude(original, ChannelDescription, logger);
        var changed = permitted.Count != original.Count;

        for (var i = 0; i < permitted.Count; i++)
        {
            var governed = Govern(permitted[i]);
            if (!ReferenceEquals(governed, permitted[i]))
            {
                permitted[i] = governed;
                changed = true;
            }
        }

        return changed ? permitted : null;
    }

    /// <summary>
    /// Returns <paramref name="tool"/> wrapped in a <see cref="GovernedAIFunction"/> when it is an
    /// unwrapped callable function, or the tool unchanged when it is already governed, is not a
    /// function, or is one of the two skill-content transport tools. Extracted for unit testing of the
    /// wrapping decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why <c>load_skill</c> and <c>read_skill_resource</c> are not governed.</strong> They are
    /// not capabilities an agent is granted — they are how the agent reads the instructions for skills it
    /// has <em>already</em> been assigned, and the provider that publishes them is built from this agent's
    /// own skills, so neither can name a skill the agent was not given. Gating them on a tool grant asks
    /// the wrong question: on a bundle run the ambient capability envelope lists the domain tools the
    /// caller may invoke, would never list a framework transport tool, and the governor refuses anything
    /// the envelope does not name — so wrapping these would leave a bundle agent unable to load the
    /// instructions of the skills it shipped with, silently, with a refusal string where its own skill
    /// body should be.
    /// </para>
    /// <para>
    /// The exemption reuses <see cref="ToolPermissionFilter.SkillDisclosureToolNames"/> rather than
    /// restating the pair, because that filter already exempts exactly these two for exactly this reason.
    /// Two copies of "which tools are content transport" is how one of them ends up out of date.
    /// <c>run_skill_script</c> is deliberately <em>not</em> in that set and is governed here: executing a
    /// skill's script is a capability, and on a bundle run it is one the caller's envelope must grant.
    /// </para>
    /// </remarks>
    internal static AITool Govern(AITool tool)
        => tool is AIFunction fn
           && tool is not GovernedAIFunction
           && !ToolPermissionFilter.SkillDisclosureToolNames.Contains(fn.Name)
            ? new GovernedAIFunction(fn)
            : tool;
}
