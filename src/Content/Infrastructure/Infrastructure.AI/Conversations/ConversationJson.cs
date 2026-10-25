using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Models.Conversations;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// The serializer settings both conversation stores persist transcripts with.
/// </summary>
/// <remarks>
/// Shared so the two implementations cannot drift into writing different payloads for the same
/// value — the enum converter in particular is the difference between a role persisted as
/// <c>"User"</c> and one persisted as <c>0</c>, which would make transcripts written by one store
/// unreadable by the other.
/// </remarks>
internal static class ConversationJson
{
    /// <summary>Camel-cased, enums as names, no indentation.</summary>
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    /// <summary>
    /// The person's conversations as export records (#902), in the shape they are stored in, so a copy is the transcript
    /// the harness holds rather than a re-interpretation of it.
    /// </summary>
    internal static IReadOnlyList<JsonElement> ToExportRecords(IEnumerable<ConversationRecord> conversations) =>
        [.. conversations.Select(c => JsonSerializer.SerializeToElement(c, Options))];
}
