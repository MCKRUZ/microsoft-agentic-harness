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
    /// unwrapped callable function, or the tool unchanged when it is already governed or is not a
    /// function. Extracted for unit testing of the wrapping decision.
    /// </summary>
    internal static AITool Govern(AITool tool)
        => tool is AIFunction fn and not GovernedAIFunction ? new GovernedAIFunction(fn) : tool;
}
