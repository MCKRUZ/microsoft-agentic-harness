using System.Collections.Frozen;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Shared keyword-matching primitives for the agent-selection strategies in this folder
/// (<see cref="CapabilityMatchStrategy"/>, <see cref="AgentMatchStrategy"/>). Splits text into
/// alphanumeric tokens and counts overlap against a keyword set — no stemming, no stop-word
/// removal, just the boundary-scan every strategy here needs.
/// </summary>
internal static class TextTokenizer
{
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
