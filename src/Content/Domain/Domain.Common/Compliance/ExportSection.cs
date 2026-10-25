using System.Text.Json;

namespace Domain.Common.Compliance;

/// <summary>What one store returned for the person, as JSON records in the store's own shape (#902).</summary>
/// <param name="Store">The store, matching its line in the manifest.</param>
/// <param name="Records">Every record the store holds for the person.</param>
public sealed record ExportSection(string Store, IReadOnlyList<JsonElement> Records);
