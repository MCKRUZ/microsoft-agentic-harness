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
/// <strong>Not part of identity:</strong> <see cref="SkillAgentOptions.AdditionalTools"/>,
/// <see cref="SkillAgentOptions.MiddlewareTypes"/>, <see cref="SkillAgentOptions.AdditionalProperties"/>
/// and <see cref="SkillAgentOptions.TraceScope"/> — object-valued, and the trace scope is minted fresh
/// per run, so comparing them would make every turn a miss. No caller of the cache sets them; one that
/// starts to must widen this type rather than rely on a hit honouring them. A new list- or
/// object-valued member would compare by reference and so rebuild every turn, which is the visible
/// signal to normalise it here too.
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
    private readonly string[] _allowedTools;

    private AgentBuildFingerprint(SkillAgentOptions options, string[] skillIds, string[] allowedTools)
    {
        _options = options;
        _skillIds = skillIds;
        _allowedTools = allowedTools;
    }

    /// <summary>Captures the build-affecting inputs of one request.</summary>
    public static AgentBuildFingerprint From(IReadOnlyList<string> skillIds, SkillAgentOptions options)
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
            options.AllowedTools is null ? [] : [.. options.AllowedTools.Order(StringComparer.Ordinal)]);

    /// <summary>Whether <paramref name="other"/> was captured from the same build inputs.</summary>
    public bool Matches(AgentBuildFingerprint other)
        => _options == other._options
           && _skillIds.AsSpan().SequenceEqual(other._skillIds)
           && _allowedTools.AsSpan().SequenceEqual(other._allowedTools);
}
