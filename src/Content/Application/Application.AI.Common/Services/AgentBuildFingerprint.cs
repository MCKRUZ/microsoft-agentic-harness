using System.Globalization;
using Domain.AI.Skills;

namespace Application.AI.Common.Services;

/// <summary>
/// The per-turn inputs that decide what an agent is built as, captured so
/// <see cref="AgentConversationCache"/> can tell a cached agent that still matches the request from
/// one that was built for something else.
/// </summary>
/// <remarks>
/// <para>
/// Covers the scalar and list-valued <see cref="SkillAgentOptions"/> members plus the skill ids:
/// everything <c>ExecuteAgentTurnCommandHandler</c> derives from the conversation's settings, the
/// agent definition and an AG-UI run's per-run deployment override. Comparison is by value, so two
/// separately built but equal option sets match. A null and an empty
/// <see cref="SkillAgentOptions.AllowedTools"/> both mean "no ceiling declared" and compare equal, and
/// the tool list is compared as a set (it is a ceiling intersected with the skills' allowlist, so its
/// order carries no meaning); skill ids stay ordered because merge order is meaningful.
/// </para>
/// <para>
/// <strong>Cost model.</strong> One entry is held per conversation, so a conversation whose requests
/// alternate between two build shapes — an AG-UI run with a per-run deployment override, then one
/// without — rebuilds on every flip, each time replacing the execution context and starting a new
/// trace. Retaining both variants would mean keying entries by fingerprint too, with matching
/// eviction; that is not done because alternating shapes are the exception.
/// </para>
/// <para>
/// <strong>Deliberately not part of identity:</strong> <see cref="SkillAgentOptions.AdditionalTools"/>,
/// <see cref="SkillAgentOptions.MiddlewareTypes"/>, <see cref="SkillAgentOptions.AdditionalProperties"/>
/// and <see cref="SkillAgentOptions.TraceScope"/>. They are object-valued, have no value equality, and
/// <see cref="SkillAgentOptions.TraceScope"/> is minted fresh per run, so including them would make
/// every turn a miss. No caller of the cache sets them; one that starts to must widen this type
/// rather than rely on a hit honouring them.
/// </para>
/// </remarks>
internal sealed class AgentBuildFingerprint
{
    private readonly string[] _skillIds;
    private readonly string[] _allowedTools;
    private readonly string?[] _scalars;

    private AgentBuildFingerprint(string[] skillIds, string[] allowedTools, string?[] scalars)
    {
        _skillIds = skillIds;
        _allowedTools = allowedTools;
        _scalars = scalars;
    }

    /// <summary>Captures the build-affecting inputs of one request.</summary>
    public static AgentBuildFingerprint From(IReadOnlyList<string> skillIds, SkillAgentOptions options)
        => new(
            [.. skillIds],
            options.AllowedTools is null ? [] : [.. options.AllowedTools.Order(StringComparer.Ordinal)],
            [
                options.AgentNameOverride,
                options.DeploymentName,
                options.AgentId,
                options.FrameworkType?.ToString(),
                options.AdditionalContext,
                options.AgentInstructions,
                options.OwningAgentId,
                options.Temperature?.ToString("R", CultureInfo.InvariantCulture),
            ]);

    /// <summary>Whether <paramref name="other"/> was captured from the same build inputs.</summary>
    public bool Matches(AgentBuildFingerprint other)
        => _scalars.AsSpan().SequenceEqual(other._scalars)
           && _skillIds.AsSpan().SequenceEqual(other._skillIds)
           && _allowedTools.AsSpan().SequenceEqual(other._allowedTools);
}
