namespace Infrastructure.AI.Telemetry.Redaction;

/// <summary>
/// Check-digit tests for the identifiers <see cref="DefaultContentRedactionFilter"/> recognises by shape (#903): a candidate
/// is redacted only when its own check digits hold, so a product code or reference number that merely looks like one is
/// left alone. Each test returns how many leading characters of the candidate form a genuine identifier, or 0.
/// </summary>
/// <remarks>
/// Returning a length rather than a yes or no lets a pattern match a little too much without leaking: an IBAN's spaced
/// groups can run into the next upper-case word ("... 0130 00 AND"), and the test then finds the identifier inside the
/// match instead of rejecting the whole of it.
/// </remarks>
internal static class IdentifierCheckDigits
{
    private const string DniLetters = "TRWAGMYFPDXBNJZSQVHLCKE";

    /// <summary>The longest prefix of <paramref name="candidate"/> that is an IBAN with valid mod-97 check digits.</summary>
    /// <param name="candidate">A regex match: two letters, two digits, then letters, digits and single spaces.</param>
    /// <returns>The prefix length in characters of <paramref name="candidate"/>, or 0.</returns>
    public static int IbanLength(string candidate)
    {
        for (var end = candidate.Length; end > 0; end--)
        {
            // A prefix counts only where an IBAN can end: the end of the match or a group boundary. Accepting any shorter
            // prefix that happens to pass mod 97 would redact part of a number and leave its last digits in clear.
            if (candidate[end - 1] == ' ' || (end < candidate.Length && candidate[end] != ' '))
                continue;
            var compact = candidate[..end].Replace(" ", string.Empty, StringComparison.Ordinal);
            if (compact.Length < 15)
                return 0;
            if (compact.Length <= 34 && Mod97(compact[4..] + compact[..4]) == 1)
                return end;
        }

        return 0;
    }

    /// <summary>Whether a Spanish DNI (8 digits and a letter) or NIE (X, Y or Z, 7 digits and a letter) has its check letter.</summary>
    /// <param name="candidate">The matched text, possibly with a space or hyphen before the letter.</param>
    /// <returns>The candidate's length when the letter is right, or 0.</returns>
    public static int SpanishIdLength(string candidate)
    {
        var compact = Compact(candidate).ToUpperInvariant();
        var digits = compact[0] switch
        {
            'X' => "0" + compact[1..^1],
            'Y' => "1" + compact[1..^1],
            'Z' => "2" + compact[1..^1],
            _ => compact[..^1],
        };
        return long.TryParse(digits, out var number) && DniLetters[(int)(number % 23)] == compact[^1] ? candidate.Length : 0;
    }

    /// <summary>
    /// Whether an Italian <i>codice fiscale</i> has its check character, including codes where digits were replaced by the
    /// letters L to V to resolve a collision (<i>omocodia</i>).
    /// </summary>
    /// <param name="candidate">Sixteen characters in the code's shape.</param>
    /// <returns>16 when the check character is right, or 0.</returns>
    public static int CodiceFiscaleLength(string candidate)
    {
        // Every function here is called from inside a regex replacement, where an exception would fail the whole
        // redaction: anything outside ASCII letters and digits is simply not an identifier.
        if (candidate.Length != 16 || !candidate.All(char.IsAsciiLetterOrDigit))
            return 0;
        var code = candidate.ToUpperInvariant();
        var sum = 0;
        for (var i = 0; i < 15; i++)
            sum += i % 2 == 0 ? OddValue(code[i]) : EvenValue(code[i]);
        return code[15] == (char)('A' + (sum % 26)) ? candidate.Length : 0;
    }

    /// <summary>
    /// Whether a French social-security number (NIR: 13 characters and a 2-digit key, Corsican departments as 2A or 2B)
    /// has its key.
    /// </summary>
    /// <param name="candidate">The matched text, possibly spaced.</param>
    /// <returns>The candidate's length when the key is right, or 0.</returns>
    public static int FrenchNirLength(string candidate)
    {
        var compact = Compact(candidate).ToUpperInvariant();
        var body = compact[..13].Replace("2A", "19", StringComparison.Ordinal).Replace("2B", "18", StringComparison.Ordinal);
        if (!long.TryParse(body, out var number) || !int.TryParse(compact[13..], out var key))
            return 0;
        return 97 - (number % 97) == key ? candidate.Length : 0;
    }

    /// <summary>
    /// Whether a Belgian national number (YY.MM.DD-SSS.CC) has its check digits, read as a 20th-century birth or, for those
    /// born from 2000, with a leading 2.
    /// </summary>
    /// <param name="candidate">The matched text, possibly punctuated.</param>
    /// <returns>The candidate's length when the check digits are right, or 0.</returns>
    public static int BelgianNationalNumberLength(string candidate)
    {
        var digits = new string([.. candidate.Where(char.IsAsciiDigit)]);
        if (digits.Length != 11 || !long.TryParse(digits[..9], out var body) || !int.TryParse(digits[9..], out var check))
            return 0;
        return 97 - (body % 97) == check || 97 - ((2_000_000_000L + body) % 97) == check ? candidate.Length : 0;
    }

    private static string Compact(string candidate) =>
        new([.. candidate.Where(c => c is not (' ' or '-' or '.'))]);

    /// <summary>The value mod 97 of a string of digits and letters, letters read as 10 to 35.</summary>
    private static int Mod97(string value)
    {
        var remainder = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c))
                remainder = (remainder * 10 + (c - '0')) % 97;
            else if (char.IsAsciiLetterUpper(c))
                remainder = (remainder * 100 + (c - 'A' + 10)) % 97;
            else
                return -1;
        }

        return remainder;
    }

    private static readonly int[] OddDigitValues = [1, 0, 5, 7, 9, 13, 15, 17, 19, 21];

    private static readonly int[] OddLetterValues =
        [1, 0, 5, 7, 9, 13, 15, 17, 19, 21, 2, 4, 18, 20, 11, 3, 6, 8, 12, 14, 16, 10, 22, 25, 24, 23];

    private static int OddValue(char c) => char.IsAsciiDigit(c) ? OddDigitValues[c - '0'] : OddLetterValues[c - 'A'];

    private static int EvenValue(char c) => char.IsAsciiDigit(c) ? c - '0' : c - 'A';
}
