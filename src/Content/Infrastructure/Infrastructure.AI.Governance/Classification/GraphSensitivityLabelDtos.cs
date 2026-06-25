using System.Text.Json.Serialization;

namespace Infrastructure.AI.Governance.Classification;

/// <summary>
/// Wire-format envelope for a Microsoft Graph collection response — the <c>value</c> array returned by
/// <c>GET /security/dataSecurityAndGovernance/sensitivityLabels</c>. Internal to the MIP provider.
/// </summary>
/// <param name="Value">The sensitivity labels in the response, or null when the body is malformed.</param>
internal sealed record GraphLabelListResponse(
    [property: JsonPropertyName("value")] IReadOnlyList<GraphSensitivityLabelDto>? Value);

/// <summary>
/// Wire-format projection of a single Microsoft Graph <c>sensitivityLabel</c> resource. Only the fields
/// the gate needs — the stable id and the display name — are bound; the rest of the resource is ignored.
/// </summary>
/// <param name="Id">The label's stable GUID, matched against the embedded label id on an asset.</param>
/// <param name="Name">The plaintext label name surfaced to the policy's label→action map.</param>
internal sealed record GraphSensitivityLabelDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name);
