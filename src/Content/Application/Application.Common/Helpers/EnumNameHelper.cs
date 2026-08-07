namespace Application.Common.Helpers;

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
/// This helper lives in <c>Application.Common</c> deliberately: it is the layer both
/// <c>Application.Core</c> (which validates these values at boot) and <c>Application.AI.Common</c>
/// (which parses them again at runtime) can reference. Before #296 the two had separate rules, so
/// the boot validator rejected <c>"3"</c> while the runtime parser accepted it as <c>High</c>.
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
    /// <see cref="Enum.IsDefined{TEnum}(TEnum)"/> cannot catch it. A <c>[Flags]</c> enum would need a
    /// different helper; none is parsed this way, and none should silently become parseable here.
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

        if (char.IsAsciiDigit(value[0]) || value[0] is '-' or '+')
            return false;

        if (value.Contains(',', StringComparison.Ordinal))
            return false;

        return Enum.TryParse(value, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }
}
