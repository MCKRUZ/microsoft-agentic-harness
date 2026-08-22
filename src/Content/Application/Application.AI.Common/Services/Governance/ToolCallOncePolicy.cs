using System.Collections.Concurrent;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// Default <see cref="IToolCallOncePolicy"/>: a concurrent set of tool names, topped up at
/// runtime by <c>ToolChainBuilder</c> as it resolves tools, and seeded — lazily, on first use —
/// from every discovered SKILL.md's own declarations.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why manifest-seeded, not runtime-registration alone.</strong> Runtime registration
/// only happens as a side effect of <c>ToolChainBuilder</c> resolving a skill's tools — which
/// only occurs once some conversation actually builds that skill. A host that mostly serves the
/// Execution API's direct-invoke or workflow-run surfaces (neither of which goes through
/// <c>ToolChainBuilder</c> at all) would answer <see cref="IsCallOnce"/> <see langword="false"/>
/// for every tool, forever, regardless of what a SKILL.md declared: enforcement would be ON in
/// configuration and silently a no-op in practice, which is worse than off, since an operator
/// reading the config believes the control is live. Seeding from
/// <see cref="ISkillMetadataRegistry"/> — every skill's declarations are already discovered and
/// cached at that layer — makes the answer independent of which skills a given process happens
/// to have built a conversation for.
/// </para>
/// <para>
/// <strong>The manifest seed answers by declared name, not resolved name — a known, narrower
/// gap than the one it closes.</strong> A <c>ToolDeclaration.Name</c> that names a first-party
/// keyed-DI tool IS the name that reaches admission, so the seed covers that case exactly. A
/// declaration that names an MCP server resolves to that server's own tool list at runtime,
/// under names the manifest alone cannot predict —
/// <c>ToolChainBuilder.RegisterSurvivingCallOnceTools</c> remains the only source of truth for
/// those, which is why runtime registration stays, as a top-up rather than being replaced. It is
/// also, independently, the only path that can verify a plugin-sourced declaration survived that
/// plugin's own AllowedTools/DeniedTools boundary — see that method's remarks — which is why this
/// seed skips plugin-sourced skills entirely rather than trying to approximate the boundary here.
/// </para>
/// <para>
/// <strong>Registered as a singleton</strong> — the set must outlive the tool-resolution scope
/// that populated it, since the admission check happens later, on a different scope, matching
/// <c>ToolBehaviorRegistry</c>'s lifetime reasoning.
/// </para>
/// <para>
/// <strong>Lazy, not eager-at-construction, and never lets a discovery failure poison every
/// subsequent call.</strong> Reading every manifest touches the filesystem
/// (<see cref="ISkillMetadataRegistry"/> discovers and caches on first access), and this type is
/// a singleton DI may construct as an incidental side effect of resolving something else
/// entirely — so the seed is computed once, on first <see cref="IsCallOnce"/> call, not in the
/// constructor. <c>Lazy&lt;T&gt;</c>'s default mode caches an exception from its factory and
/// rethrows it on every later access; a manifest-discovery failure (an unregistered plugin
/// registry, a malformed SKILL.md) would then turn every subsequent tool call in the process
/// into an unhandled fault, which is exactly the failure shape this feature's own ledger goes
/// out of its way to avoid on the write side. <see cref="ComputeManifestSeed"/> therefore catches
/// internally and degrades to an empty seed (runtime-registration-only — the behavior before
/// manifest seeding existed, not a new gap) rather than letting the exception reach
/// <c>Lazy&lt;T&gt;</c> at all.
/// </para>
/// </remarks>
public sealed class ToolCallOncePolicy : IToolCallOncePolicy
{
    private readonly ConcurrentDictionary<string, byte> _callOnce = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISkillMetadataRegistry? _skillRegistry;
    private readonly ILogger<ToolCallOncePolicy> _logger;
    private readonly Lazy<IReadOnlySet<string>> _manifestSeed;

    /// <summary>Initializes a new instance.</summary>
    /// <param name="logger">Records a manifest-discovery failure that degraded the seed to empty.</param>
    /// <param name="skillRegistry">
    /// Source for the manifest-declared seed, or <see langword="null"/> when no skill discovery is
    /// composed (matching how <see cref="IToolCallLedger"/> is optional on <c>CallOnceGate</c> for
    /// the identical "Application.AI.Common must stay composable alone" reason). Absent, this type
    /// falls back to runtime-only registration.
    /// </param>
    public ToolCallOncePolicy(ILogger<ToolCallOncePolicy> logger, ISkillMetadataRegistry? skillRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _skillRegistry = skillRegistry;
        _manifestSeed = new Lazy<IReadOnlySet<string>>(ComputeManifestSeed);
    }

    /// <inheritdoc />
    public void Register(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return;

        _callOnce.TryAdd(toolName, 0);
    }

    /// <inheritdoc />
    public bool IsCallOnce(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName)
        && (_callOnce.ContainsKey(toolName) || _manifestSeed.Value.Contains(toolName));

    private HashSet<string> ComputeManifestSeed()
    {
        var seed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_skillRegistry is null)
            return seed;

        try
        {
            foreach (var skill in _skillRegistry.GetAll())
            {
                // A plugin-sourced skill's own AllowedTools/DeniedTools boundary is applied by
                // ToolChainBuilder against a RESOLVED tool list — it cannot be consulted here, over a
                // bare manifest scan, without re-implementing that resolution and filtering logic a
                // second time. Skipping plugin-sourced skills means a plugin's call-once declaration is
                // enforced only via ToolChainBuilder.RegisterSurvivingCallOnceTools, the one path that
                // can verify the tool actually survived the boundary — never from mere manifest
                // discovery, which would let a denied tool's name poison this process-global seed
                // before the skill is ever built for a real conversation.
                if (!string.IsNullOrEmpty(skill.PluginSource))
                    continue;

                if (skill.ToolDeclarations is not { Count: > 0 } declarations)
                    continue;

                foreach (var declaration in declarations)
                {
                    if (declaration.CallOncePerConversation && !string.IsNullOrWhiteSpace(declaration.Name))
                        seed.Add(declaration.Name);
                }
            }
        }
        catch (Exception ex)
        {
            // Degrade to empty (runtime-registration-only) rather than let a discovery failure
            // reach Lazy<T> and poison every subsequent IsCallOnce call for the process lifetime —
            // see this type's remarks. Logged once, at the moment discovery actually failed.
            _logger.LogError(ex,
                "Failed to seed the call-once policy from discovered SKILL.md manifests; call-once " +
                "enforcement falls back to runtime registration only for this process.");
            seed.Clear();
        }

        return seed;
    }
}
