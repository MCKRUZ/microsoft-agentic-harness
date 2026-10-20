using System.Globalization;
using System.Text.Json;

namespace Infrastructure.AI.Audit;

/// <summary>
/// A record the audit archiver saw at the head of a log's live chain, stored in the write-once archive (#416). The
/// hashes are not keyed, so whoever controls the live folder could rebuild the records not yet archived and the
/// application's own checkpoint with them; a witness is a checkpoint they cannot touch. Every later check requires the
/// chain to still hold this hash at this sequence, and because each record's hash covers the one before it, that
/// pins everything up to it.
/// </summary>
/// <param name="Sequence">The record's sequence.</param>
/// <param name="Hash">The record's hash.</param>
/// <param name="File">The file it was in.</param>
/// <param name="WitnessedAt">When the archiver saw it.</param>
internal sealed record AuditWitness(long Sequence, string Hash, string File, DateTimeOffset WitnessedAt)
{
    /// <summary>The folder, inside a log's archive folder, holding its witnesses.</summary>
    public const string Folder = "witnesses";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The file name for a witness at <paramref name="sequence"/>: zero-padded, so the newest sorts last.</summary>
    /// <param name="sequence">The witnessed sequence.</param>
    /// <returns>The file name.</returns>
    public static string FileName(long sequence) => sequence.ToString("D19", CultureInfo.InvariantCulture) + ".json";

    /// <summary>Whether <paramref name="name"/> is a witness file name.</summary>
    /// <param name="name">A file name.</param>
    /// <returns>Whether it is one.</returns>
    public static bool IsFileName(string name) =>
        name.Length == 24 && name.EndsWith(".json", StringComparison.Ordinal) && name[..19].All(char.IsAsciiDigit);

    /// <summary>The witness's stored form.</summary>
    /// <returns>UTF-8 JSON.</returns>
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>Reads a stored witness.</summary>
    /// <param name="bytes">UTF-8 JSON.</param>
    /// <returns>The witness, or <c>null</c> when it is not one.</returns>
    public static AuditWitness? From(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<AuditWitness>(bytes, Json) is { Sequence: >= 0, Hash.Length: 64 } witness ? witness : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
