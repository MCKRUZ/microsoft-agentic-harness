using Domain.Common.Helpers;
using Domain.AI.Changes;

namespace Application.Core.CQRS.Autonomy;

/// <summary>
/// Shared validation constants, failure messages, and parsing rules for the autonomy governance
/// read surface. Centralized so the tier-read and decision-preview paths agree on what a
/// well-formed subagent type, enum name, and skill key look like — and so the FluentValidation
/// boundary and the handlers' defense-in-depth re-checks emit the identical message for the
/// identical problem.
/// </summary>
public static class AutonomyValidationRules
{
    /// <summary>
    /// Failure message for a subagent type name that does not name a defined
    /// <see cref="Domain.AI.Agents.SubagentType"/> member. Maps to 404 at the HTTP boundary.
    /// Deliberately static — it never echoes the caller-supplied value.
    /// </summary>
    public const string UnknownSubagentTypeMessage = "No subagent type with the given name exists.";

    /// <summary>
    /// Failure message for a blast radius value that does not name a defined
    /// <see cref="BlastRadius"/> member. Used by both the validator and the handler's
    /// defense-in-depth re-check so the two paths cannot drift.
    /// </summary>
    public static readonly string InvalidBlastRadiusMessage =
        $"BlastRadius must be one of: {string.Join(", ", Enum.GetNames<BlastRadius>())}.";

    /// <summary>
    /// Failure message for a target kind value that does not name a defined
    /// <see cref="ChangeTargetKind"/> member. Used by both the validator and the handler's
    /// defense-in-depth re-check so the two paths cannot drift.
    /// </summary>
    public static readonly string InvalidTargetKindMessage =
        $"TargetKind must be one of: {string.Join(", ", Enum.GetNames<ChangeTargetKind>())}.";

    /// <summary>
    /// Maximum accepted length for an enum-name wire value (subagent type, blast radius,
    /// target kind). The longest real member name is far shorter; 64 characters bounds log
    /// lines and ProblemDetails payloads without ever rejecting a legitimate name.
    /// </summary>
    public const int MaxEnumNameLength = 64;

    /// <summary>
    /// Maximum accepted skill-key length. Skill keys are operator-authored identifiers used
    /// in <c>GradedAutonomyConfig</c> lookups; 256 characters comfortably covers realistic
    /// keys while bounding the evaluator's reason strings and audit lines.
    /// </summary>
    public const int MaxSkillKeyLength = 256;

    /// <summary>
    /// Parses an enum member by <em>name</em>, case-insensitively, rejecting numeric forms.
    /// </summary>
    /// <typeparam name="TEnum">The enum type to parse.</typeparam>
    /// <param name="value">The candidate wire value.</param>
    /// <param name="parsed">The parsed member when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> names a defined member.</returns>
    /// <remarks>
    /// <para>
    /// The rule itself lives in <see cref="EnumNameHelper.TryParseName{TEnum}"/>, in the layer that
    /// <c>Application.AI.Common</c> can also reference. This stays as the autonomy read surface's
    /// name for it: the wire contract is names only, and that statement belongs beside the messages
    /// that report a violation of it.
    /// </para>
    /// <para>
    /// It was NOT always shared, and the divergence had teeth — the runtime approval router parsed
    /// the same governance setting with a bare <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>,
    /// which accepts any integer string including one outside the defined range (#296).
    /// </para>
    /// </remarks>
    public static bool TryParseEnumName<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum =>
        EnumNameHelper.TryParseName(value, out parsed);
}
