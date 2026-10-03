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
/// separately built but equal option sets match.
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
internal sealed class AgentBuildFingerprint : IEquatable<AgentBuildFingerprint>
{
    private readonly string[] _skillIds;
    private readonly string[] _allowedTools;
    private readonly string?[] _scalars;
    private readonly bool _hasAllowedTools;

    private AgentBuildFingerprint(
        string[] skillIds, string[] allowedTools, bool hasAllowedTools, string?[] scalars)
    {
        _skillIds = skillIds;
        _allowedTools = allowedTools;
        _hasAllowedTools = hasAllowedTools;
        _scalars = scalars;
    }

    /// <summary>Captures the build-affecting inputs of one request.</summary>
    public static AgentBuildFingerprint From(IReadOnlyList<string> skillIds, SkillAgentOptions options)
        => new(
            [.. skillIds],
            options.AllowedTools is null ? [] : [.. options.AllowedTools],
            // Null ("no ceiling declared") and empty are the same to the factory, but keeping the
            // distinction costs nothing and avoids deciding that here.
            options.AllowedTools is not null,
            [
                options.AgentNameOverride,
                options.DeploymentName,
                options.AgentId,
                options.FrameworkType?.ToString(),
                options.AdditionalContext,
                options.AgentInstructions,
                options.OwningAgentId,
                options.Temperature?.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            ]);

    /// <inheritdoc />
    public bool Equals(AgentBuildFingerprint? other)
        => other is not null
           && _hasAllowedTools == other._hasAllowedTools
           && _scalars.AsSpan().SequenceEqual(other._scalars)
           && _skillIds.AsSpan().SequenceEqual(other._skillIds)
           && _allowedTools.AsSpan().SequenceEqual(other._allowedTools);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as AgentBuildFingerprint);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_skillIds.Length, _allowedTools.Length);
}
