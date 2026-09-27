namespace Domain.Common.Helpers;

/// <summary>
/// Recognising and canonicalising identifiers that are GUIDs, for the case where a value is
/// <em>validated</em> in one place and <em>sent somewhere</em> in another.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Guid.TryParse(string, out Guid)"/> accepts several spellings of the same value — braced
/// (<c>{…}</c>), parenthesised, dashless, and hex — and callers commonly trim before parsing. So a
/// configured id can pass a validation rule and still not be the form a remote service compares
/// against. Splitting the accept rule and the render rule across two files is how that gap opens; both
/// live here so a validator and the code that publishes the value cannot disagree.
/// </para>
/// <para>
/// Pass-through, not throw, for a non-GUID: these are used on identifiers that are conventionally GUIDs
/// but not required to be, so a caller that only wants canonical form when one is available gets it
/// without having to pre-check.
/// </para>
/// </remarks>
public static class GuidId
{
    /// <summary>
    /// Whether <paramref name="value"/> is a non-blank GUID in any of the forms
    /// <see cref="Guid.TryParse(string, out Guid)"/> accepts, ignoring surrounding whitespace.
    /// </summary>
    /// <remarks>
    /// Intended for validation rules. Anything this accepts, <see cref="Canonicalize"/> renders in the
    /// canonical form — which is the property that keeps the checked value and the published value the
    /// same.
    /// </remarks>
    public static bool IsGuid(string? value)
        => !string.IsNullOrWhiteSpace(value) && Guid.TryParse(value.Trim(), out _);

    /// <summary>
    /// Renders <paramref name="value"/> in the canonical hyphenated GUID form when it is a GUID, and
    /// returns it unchanged when it is not.
    /// </summary>
    /// <remarks>
    /// Intended for the moment a value leaves the process. Blank and non-GUID inputs are returned as
    /// given, so this never invents a value a caller did not supply.
    /// </remarks>
    public static string? Canonicalize(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed.ToString("D") : value;
}
