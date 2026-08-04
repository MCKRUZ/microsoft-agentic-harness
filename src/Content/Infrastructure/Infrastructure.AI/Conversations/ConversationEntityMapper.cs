using System.Text.Json;
using Application.AI.Common.Models.Conversations;
using Infrastructure.AI.Persistence.Entities;

namespace Infrastructure.AI.Conversations;

/// <summary>
/// Translates between the persisted conversation rows and the DTOs
/// <see cref="Application.AI.Common.Interfaces.AI.IConversationStore"/> hands its callers.
/// </summary>
/// <remarks>
/// Separate from <see cref="EfCoreConversationStore"/> so the store reads as the queries and writes
/// it performs, with the row-shape translation — the part with no behaviour of its own — out of the
/// way. Values nothing queries inside (settings, telemetry, tool calls, widget specs) are held as
/// JSON; everything a query touches is a real column.
/// </remarks>
internal static class ConversationEntityMapper
{
    /// <summary>Builds the caller-facing record from a header row and its already-mapped messages.</summary>
    internal static ConversationRecord ToRecord(
        ConversationEntity entity,
        IReadOnlyList<ConversationMessage> messages) =>
        new(
            Id: entity.Id,
            AgentName: entity.AgentName,
            UserId: entity.UserId,
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt,
            Messages: messages,
            Title: entity.Title,
            Settings: Deserialize<ConversationSettings>(entity.SettingsJson),
            ObservabilitySessionId: entity.ObservabilitySessionId,
            Telemetry: Deserialize<TelemetryAccumulator>(entity.TelemetryJson));

    /// <summary>Builds the caller-facing message from one message row.</summary>
    internal static ConversationMessage ToMessage(ConversationMessageEntity entity) =>
        new(
            Id: entity.MessageId,
            Role: entity.Role,
            Content: entity.Content,
            Timestamp: entity.Timestamp,
            ToolCalls: Deserialize<List<ToolCallRecord>>(entity.ToolCallsJson),
            Widget: Deserialize<WidgetSpec>(entity.WidgetJson));

    /// <summary>
    /// Builds the row to insert for an appended message. The ordinal is left unset — the database
    /// assigns it, which is what makes concurrent appends safe.
    /// </summary>
    internal static ConversationMessageEntity ToEntity(string conversationId, ConversationMessage message) =>
        new()
        {
            ConversationId = conversationId,
            // An empty id is normalised on the way in rather than backfilled on the way out, which
            // is how the file-backed store had to handle records written before the column existed.
            // Either way a caller that supplied none reads back a real, stable id.
            MessageId = message.Id == Guid.Empty ? Guid.NewGuid() : message.Id,
            Role = message.Role,
            Content = message.Content,
            Timestamp = message.Timestamp,
            ToolCallsJson = Serialize(message.ToolCalls),
            WidgetJson = Serialize(message.Widget),
        };

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, ConversationJson.Options);

    private static T? Deserialize<T>(string? json) where T : class =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, ConversationJson.Options);
}
