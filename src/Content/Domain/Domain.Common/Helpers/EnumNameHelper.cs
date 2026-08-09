namespace Domain.Common.Helpers;

/// <summary>
/// Parses enum values supplied as configuration or wire data, by <em>name</em> only.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists rather than calling <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>
/// directly.</strong> That method succeeds for any integer string, including one outside the defined
/// range. <c>Enum.TryParse&lt;BlastRadius&gt;("99", …)</c> returns <see langword="true"/> and hands
/// back a <c>BlastRadius</c> whose value is 99 — a member that does not exist. Nothing downstream
/// has any reason to suspect it: comparisons still compile, <c>ToString()</c> returns "99", and a
/// severity ordering like <c>radius &gt;= threshold</c> silently stops ever being true.
/// </para>
/// <para>
/// Configuration is exactly where this bites. A typo in a governance setting should be a startup
/// error or a logged fallback, never a value that is accepted, is not a real member, and quietly
/// changes a safety comparison.
/// </para>
/// <para>
/// This helper lives in <c>Domain.Common</c> deliberately: it is the innermost layer, referenced by
/// every other one, so <em>every</em> layer that reads an enum by name can reach the same rule.
/// Before #296 the boot validator and the runtime parser had separate rules, so the validator
/// rejected <c>"3"</c> while the parser accepted it as <c>High</c>.
/// </para>
/// <para>
/// It sat in <c>Application.Common</c> until #312 moved it here. That placement was one ring too far
/// out to be reachable from the Domain, and the Domain — unable to reference it — had hand-rolled two
/// weaker copies of the same rule rather than go without. <c>Domain.Common</c> has no project
/// references of its own, so the move adds no dependency anywhere and bends no arrows: the body uses
/// only <see cref="Enum"/>, <see cref="char"/> and <see cref="string"/>.
/// </para>
/// </remarks>
public static class EnumNameHelper
{
    /// <summary>
    /// Parses an enum member by name, case-insensitively, rejecting numeric forms and undefined
    /// values.
    /// </summary>
    /// <typeparam name="TEnum">The enum type to parse.</typeparam>
    /// <param name="value">The candidate configuration or wire value.</param>
    /// <param name="parsed">The parsed member when the method returns <see langword="true"/>.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="value"/> names a defined member of
    /// <typeparamref name="TEnum"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Three checks, each catching something the others do not:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// A leading digit or sign refuses the numeric wire form even when the number happens to name a
    /// real member. <c>"2"</c> and <c>"High"</c> must not be interchangeable, or a value can mean
    /// one thing to a boot validator and another to a runtime parser.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// A comma refuses a flag combination. <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>
    /// reads <c>"Low,High"</c> as a bitwise OR, and when that OR happens to land on a defined member
    /// it is indistinguishable from having named that member directly —
    /// <see cref="Enum.IsDefined{TEnum}(TEnum)"/> cannot catch it. A <c>[Flags]</c> enum may still be
    /// parsed here <em>one member at a time</em> (as <c>ToolCapability</c> is, from a list of names);
    /// what this refuses is a combination smuggled into a single value, which is where the
    /// indistinguishability bites.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <see cref="Enum.IsDefined{TEnum}(TEnum)"/> catches everything else outside the defined range.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    public static bool TryParseName<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        // Trim before the guards, not after. Enum.TryParse trims its own input, so an untrimmed
        // guard inspects a different first character than the parser does: " 2" begins with a space,
        // clears the digit check, and then parses as the numeric form of the member with value 2.
        // One stray space in a config file was enough to reopen the hole this helper closes.
        var candidate = value.Trim();

        if (char.IsAsciiDigit(candidate[0]) || candidate[0] is '-' or '+')
            return false;

        if (candidate.Contains(',', StringComparison.Ordinal))
            return false;

        return Enum.TryParse(candidate, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }
}
