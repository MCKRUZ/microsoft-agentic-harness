using System.Text.Json;

namespace Infrastructure.AI.Audit;

/// <summary>
/// An operator's decision, stored in the write-once archive, that one live audit file with exactly these bytes may be
/// archived although a line in it does not chain (#416): typically a line torn by a crash mid-write, which no writer
/// can repair. Within that file only, lines that do not chain are stepped over, as the writer does; the bytes are pinned
/// by hash, so the acceptance covers nothing written or changed afterwards.
/// </summary>
/// <param name="File">The live file's name.</param>
/// <param name="Sha256">The lowercase hex SHA-256 of its bytes.</param>
/// <param name="Reason">Why it was accepted, for the record.</param>
/// <param name="AcceptedAt">When.</param>
internal sealed record AuditAcceptance(string File, string Sha256, string Reason, DateTimeOffset AcceptedAt)
{
    /// <summary>The folder, inside a log's archive folder, holding its acceptances.</summary>
    public const string Folder = "accepted";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The stored form.</summary>
    /// <returns>UTF-8 JSON.</returns>
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>Reads a stored acceptance.</summary>
    /// <param name="bytes">UTF-8 JSON.</param>
    /// <returns>The acceptance, or <c>null</c> when it is not one.</returns>
    public static AuditAcceptance? From(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<AuditAcceptance>(bytes, Json) is { Sha256.Length: 64 } acceptance ? acceptance : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
