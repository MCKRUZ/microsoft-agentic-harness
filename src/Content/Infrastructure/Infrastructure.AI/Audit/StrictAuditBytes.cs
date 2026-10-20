using System.Text;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Turns an audit log file's bytes into its lines, refusing anything <see cref="HashChainedJsonlWriter"/> never writes
/// (#416). The writer emits UTF-8 without a byte-order mark, one record per <c>\n</c>, and never a <c>\r</c> (it
/// refuses payloads holding one). With those excluded, splitting on <c>\n</c> here and the application's
/// <see cref="StreamReader.ReadLineAsync(CancellationToken)"/> see exactly the same lines, so the archiver can never
/// seal into write-once storage a file the application would read differently and report broken for ever.
/// </summary>
internal static class StrictAuditBytes
{
    /// <summary>The longest line accepted: the writer's own hint check gives up on records longer than this.</summary>
    public const int MaxLineBytes = 1024 * 1024;

    /// <summary>The UTF-8 byte-order mark: EF BB BF.</summary>
    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The lines of <paramref name="content"/>, or why it is not something the writer wrote.</summary>
    /// <param name="content">The file's bytes.</param>
    /// <param name="lines">Its lines, without line ends; a trailing unterminated fragment is the last line.</param>
    /// <param name="problem">Why the bytes are refused.</param>
    /// <returns>Whether the bytes are acceptable.</returns>
    public static bool TryRead(ReadOnlySpan<byte> content, out IReadOnlyList<string> lines, out string? problem)
    {
        lines = [];
        if (content.StartsWith(ByteOrderMark))
        {
            problem = "it starts with a byte-order mark, which the writer never writes";
            return false;
        }

        if (content.IndexOf((byte)'\r') is var cr and >= 0)
        {
            problem = $"it holds a carriage return at byte {cr}, which the writer never writes";
            return false;
        }

        string text;
        try
        {
            text = Strict.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            problem = "it is not valid UTF-8";
            return false;
        }

        var split = text.Split('\n');
        if (split.Any(l => Encoding.UTF8.GetByteCount(l) > MaxLineBytes))
        {
            problem = $"it holds a line longer than {MaxLineBytes} bytes";
            return false;
        }

        // "a\nb\n" splits to [a, b, ""]: drop the empty piece after the final line end.
        lines = content.Length > 0 && content[^1] == (byte)'\n' ? split[..^1] : split;
        problem = null;
        return true;
    }
}
