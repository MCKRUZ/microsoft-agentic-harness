using System.Collections.Frozen;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Shared keyword-matching primitives for the agent-selection strategies in this folder
/// (<see cref="CapabilityMatchStrategy"/>, <see cref="AgentMatchStrategy"/>). Splits text into
/// alphanumeric tokens and counts overlap against a keyword set.
/// </summary>
internal static class TextTokenizer
{
    /// <summary>
    /// Common English filler words that carry no discriminating signal for free-text description
    /// matching. Excluded only by <see cref="TokenizeMeaningful"/> — <see cref="Tokenize"/> itself
    /// stays raw so <see cref="CapabilityMatchStrategy"/>'s curated keyword sets (none of which are
    /// stop words) keep matching exactly as before.
    /// </summary>
    private static readonly FrozenSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
        "this", "that", "these", "those", "it", "its",
        "and", "or", "but", "if", "then", "so", "as",
        "to", "of", "in", "on", "at", "for", "with", "from", "by", "about",
        "i", "you", "he", "she", "we", "they", "me", "my", "your",
        "do", "does", "did", "can", "could", "will", "would", "should",
        "not", "no", "yes",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Splits <paramref name="text"/> into contiguous runs of letters/digits.</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var start = -1;

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                if (start < 0) start = i;
            }
            else
            {
                if (start >= 0)
                {
                    tokens.Add(text[start..i]);
                    start = -1;
                }
            }
        }

        if (start >= 0)
            tokens.Add(text[start..]);

        return tokens;
    }

    /// <summary>
    /// <see cref="Tokenize"/>, with common filler words removed — for matching against free text
    /// (a description, a user message) where a shared "the" or "is" is not a meaningful signal.
    /// </summary>
    public static List<string> TokenizeMeaningful(string text) =>
        Tokenize(text).Where(t => !StopWords.Contains(t)).ToList();

    /// <summary>Counts how many of <paramref name="tokens"/> appear in <paramref name="keywords"/>.</summary>
    public static int CountMatches(List<string> tokens, FrozenSet<string> keywords)
    {
        var count = 0;
        foreach (var token in tokens)
        {
            if (keywords.Contains(token))
                count++;
        }

        return count;
    }
}
