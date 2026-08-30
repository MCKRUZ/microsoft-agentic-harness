using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Application.AI.Common.StructuredOutput;

/// <summary>
/// Generic guards over a <see cref="StructuredOutputContract"/>'s schema against its own
/// <see cref="StructuredOutputContract.ResponseType"/> — usable from any test project that can see
/// the response type (first-party contracts live in <c>Application.AI.Common.Tests</c>; a contract
/// for an <see langword="internal"/> DTO in another assembly is tested from that assembly's own
/// test project, which already has <c>InternalsVisibleTo</c> access to build the contract in the
/// first place).
/// </summary>
public static class StructuredOutputSchemaValidation
{
    /// <summary>
    /// Compares <paramref name="contract"/>'s schema <c>properties</c> and <c>required</c> sets
    /// against <see cref="StructuredOutputContract.ResponseType"/>'s serializable members, in both
    /// directions. Returns a human-readable drift description per mismatch; an empty list means no
    /// drift. Checking both directions matters — a schema property with no CLR member, and a CLR
    /// member with no schema property, are different bugs and neither implies the other.
    /// </summary>
    public static IReadOnlyList<string> FindDrift(StructuredOutputContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var drift = new List<string>();
        var members = GetSerializableMembers(contract.ResponseType, contract.SerializerOptions);
        var memberNames = members.Select(m => m.JsonName).ToHashSet(StringComparer.Ordinal);
        var requiredMemberNames = members.Where(m => m.IsRequired).Select(m => m.JsonName)
            .ToHashSet(StringComparer.Ordinal);

        if (!contract.Schema.TryGetProperty("properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            if (members.Count > 0)
                drift.Add($"{contract.ResponseType.Name}: schema has no 'properties' object, but the type has {members.Count} serializable member(s).");
            return drift;
        }

        var schemaPropertyNames = propertiesElement.EnumerateObject().Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var schemaName in schemaPropertyNames)
            if (!memberNames.Contains(schemaName))
                drift.Add($"{contract.ResponseType.Name}: schema property '{schemaName}' has no matching CLR member.");

        foreach (var member in members)
            if (!schemaPropertyNames.Contains(member.JsonName))
                drift.Add($"{contract.ResponseType.Name}: CLR member '{member.ClrName}' (JSON '{member.JsonName}') has no matching schema property.");

        var schemaRequired = contract.Schema.TryGetProperty("required", out var requiredElement)
                && requiredElement.ValueKind == JsonValueKind.Array
            ? requiredElement.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];

        foreach (var name in schemaRequired)
            if (!requiredMemberNames.Contains(name))
                drift.Add($"{contract.ResponseType.Name}: schema marks '{name}' required, but the CLR member is not `required`/[JsonRequired].");

        foreach (var name in requiredMemberNames)
            if (!schemaRequired.Contains(name))
                drift.Add($"{contract.ResponseType.Name}: CLR member '{name}' is `required`/[JsonRequired], but the schema does not mark it required.");

        return drift;
    }

    /// <summary>
    /// Depth-first walk of <paramref name="schema"/> returning the JSON pointer of every node whose
    /// <c>"type"</c> is <c>"array"</c> and which declares no <c>"items"</c> schema — the single
    /// shape a model most reliably mis-fills when a schema under-constrains it.
    /// </summary>
    public static IReadOnlyList<string> FindArraysWithoutItems(JsonElement schema)
    {
        var offenders = new List<string>();
        Walk(schema, "#", offenders);
        return offenders;
    }

    private static void Walk(JsonElement node, string pointer, List<string> offenders)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (node.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
            && typeElement.GetString() == "array"
            && !node.TryGetProperty("items", out _))
        {
            offenders.Add(pointer);
        }

        foreach (var property in node.EnumerateObject())
            Walk(property.Value, $"{pointer}/{property.Name}", offenders);
    }

    private static IReadOnlyList<SerializableMember> GetSerializableMembers(Type type, JsonSerializerOptions options)
    {
        var namingPolicy = options.PropertyNamingPolicy;
        var members = new List<SerializableMember>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var ignore = property.GetCustomAttribute<JsonIgnoreAttribute>();
            if (ignore is not null && ignore.Condition == JsonIgnoreCondition.Always)
                continue;
            if (property.GetIndexParameters().Length > 0)
                continue;

            var explicitName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            var jsonName = explicitName ?? namingPolicy?.ConvertName(property.Name) ?? property.Name;

            var isRequired = property.GetCustomAttribute<JsonRequiredAttribute>() is not null
                || IsRequiredMember(property);

            members.Add(new SerializableMember(property.Name, jsonName, isRequired));
        }

        return members;
    }

    // `required` (the C# keyword, not [JsonRequired]) shows up to reflection as
    // System.Runtime.CompilerServices.RequiredMemberAttribute on the property.
    private static bool IsRequiredMember(PropertyInfo property) =>
        property.GetCustomAttributes(inherit: false)
            .Any(a => a.GetType().FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute");

    private sealed record SerializableMember(string ClrName, string JsonName, bool IsRequired);
}
