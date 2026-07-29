using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// The single <see cref="JsonSerializerOptions"/> instance the durable governance-state store
/// uses for every payload column (escalation requests, decisions, outcomes, and change
/// proposals), so all rows in the database share one wire shape.
/// </summary>
/// <remarks>
/// Enums serialize as their names (resilient to reordering, matching the JSONL audit stores'
/// convention) and the polymorphic <c>ChangeTarget</c> hierarchy round-trips through
/// <see cref="ChangeTargetJsonConverter"/>. Property names keep their C# casing;
/// case-insensitive reads tolerate historical rows if that ever changes.
/// </remarks>
public static class GovernanceStateJson
{
    /// <summary>The shared serializer options for governance-state payload columns.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(),
            new ChangeTargetJsonConverter()
        }
    };
}
