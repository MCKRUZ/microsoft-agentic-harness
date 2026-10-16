using Domain.AI.Skills;

namespace Application.AI.Common.Services;

/// <summary>
/// What an agent was built from, kept beside it in <see cref="AgentConversationCache"/> so a later
/// request that asks for something else is rebuilt rather than served the stale agent.
/// </summary>
/// <remarks>
/// <para>
/// Derived from <see cref="SkillAgentOptions"/> itself rather than a hand-kept list of its members:
/// the record's own value equality compares every scalar, so a member added later is part of the
/// comparison by default and forgetting it costs an extra rebuild, never a stale agent. Only what
/// record equality cannot compare is normalised out of the key and handled here:
/// <see cref="SkillAgentOptions.AllowedTools"/> (a list, compared by reference) is held separately as a
/// sorted set — it is a ceiling intersected with the skills' allowlist, so order carries no meaning and
/// null and empty both mean "no ceiling" — and the skill ids stay ordered, because merge order matters.
/// </para>
/// <para>
/// <strong>A skill's own text is a build input too (#792).</strong> Each requested skill contributes the
/// <see cref="SkillDefinition.ContentHash"/> of the definition it resolves to, so a SKILL.md edited while
/// the conversation is live rebuilds the agent on the next turn, while a registry reload that left the
/// text alone does not. A definition with no hash (one not parsed from document text) cannot be told
/// apart from its earlier self, so for those the cache behaves as it did before: it reuses the agent
/// until the sliding expiry or an explicit <c>Invalidate</c>.
/// </para>
/// <para>
/// <strong>Fail-safe for what cannot be compared:</strong> <see cref="SkillAgentOptions.AdditionalTools"/>,
/// <see cref="SkillAgentOptions.MiddlewareTypes"/> and <see cref="SkillAgentOptions.AdditionalProperties"/>
/// are object-valued, so a request that sets any of them is never reused — it rebuilds every turn rather
/// than risk a hit that ignores a change to tooling or middleware. No caller of the cache sets them
/// today. <see cref="SkillAgentOptions.TraceScope"/> is ignored outright: it is minted fresh per run and
/// is not a build input. A new list- or object-valued member would compare by reference and so also
/// rebuild every turn, which is the visible signal to normalise it here.
/// </para>
/// <para>
/// One entry is held per conversation, so a conversation alternating between two build shapes (an
/// AG-UI run with a per-run deployment override, then one without) rebuilds on every flip.
/// </para>
/// </remarks>
internal sealed class AgentBuildFingerprint
{
    private readonly SkillAgentOptions _options;
    private readonly string[] _skillIds;
    private readonly string?[] _skillContentHashes;
    private readonly string[] _allowedTools;
    private readonly bool _hasUncomparableInputs;

    private AgentBuildFingerprint(
        SkillAgentOptions options,
        string[] skillIds,
        string?[] skillContentHashes,
        string[] allowedTools,
        bool hasUncomparableInputs)
    {
        _options = options;
        _skillIds = skillIds;
        _skillContentHashes = skillContentHashes;
        _allowedTools = allowedTools;
        _hasUncomparableInputs = hasUncomparableInputs;
    }

    /// <summary>Captures the build-affecting inputs of one request.</summary>
    /// <param name="skillIds">The requested skill ids, in merge order.</param>
    /// <param name="skillContentHashes">
    /// <see cref="SkillDefinition.ContentHash"/> of the definition each id resolves to, in the same order;
    /// null for an id that did not resolve or whose definition carries no hash.
    /// </param>
    /// <param name="options">The request's build options.</param>
    public static AgentBuildFingerprint From(
        IReadOnlyList<string> skillIds, IReadOnlyList<string?> skillContentHashes, SkillAgentOptions options)
        => new(
            options with
            {
                AllowedTools = null,
                AdditionalTools = null,
                MiddlewareTypes = null,
                AdditionalProperties = null,
                TraceScope = null,
            },
            [.. skillIds],
            [.. skillContentHashes],
            options.AllowedTools is null ? [] : [.. options.AllowedTools.Order(StringComparer.Ordinal)],
            options.AdditionalTools is { Count: > 0 }
                || options.MiddlewareTypes is { Count: > 0 }
                || options.AdditionalProperties is { Count: > 0 });

    /// <summary>Whether <paramref name="other"/> was captured from the same build inputs.</summary>
    public bool Matches(AgentBuildFingerprint other)
        => !_hasUncomparableInputs
           && !other._hasUncomparableInputs
           && _options == other._options
           && _skillIds.AsSpan().SequenceEqual(other._skillIds)
           && _skillContentHashes.AsSpan().SequenceEqual(other._skillContentHashes)
           && _allowedTools.AsSpan().SequenceEqual(other._allowedTools);
}
