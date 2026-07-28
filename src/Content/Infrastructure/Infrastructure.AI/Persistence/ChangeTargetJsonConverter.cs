using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.AI.Changes;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// System.Text.Json converter for the polymorphic <see cref="ChangeTarget"/> hierarchy, used
/// by the durable governance-state store. Writes a <c>kind</c> discriminator (the
/// <see cref="ChangeTargetKind"/> enum name) plus each concrete target's constructor fields,
/// and reconstructs the target through its public constructor so derived values
/// (<see cref="ChangeTarget.DisplayName"/>, <see cref="ChangeTarget.CanonicalKey"/>) are
/// rebuilt rather than trusted from storage.
/// </summary>
/// <remarks>
/// Covers the three built-in targets (<see cref="GitRepoTarget"/>,
/// <see cref="KubernetesResourceTarget"/>, <see cref="IacDeploymentTarget"/>). A consumer who
/// adds a <see cref="ChangeTarget"/> subclass and enables the durable proposal store must
/// extend this converter (or register a replacement <c>JsonSerializerOptions</c>-bearing
/// store); serialization of an unknown subclass throws <see cref="NotSupportedException"/> at
/// save time — loudly, before any state is half-persisted.
/// </remarks>
public sealed class ChangeTargetJsonConverter : JsonConverter<ChangeTarget>
{
    private const string KindProperty = "kind";

    /// <inheritdoc />
    public override ChangeTarget Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (!root.TryGetProperty(KindProperty, out var kindElement) ||
            !Enum.TryParse<ChangeTargetKind>(kindElement.GetString(), ignoreCase: true, out var kind))
        {
            throw new JsonException(
                "ChangeTarget JSON is missing a recognizable 'kind' discriminator.");
        }

        return kind switch
        {
            ChangeTargetKind.GitRepo => new GitRepoTarget(
                GetString(root, "repoUrl"),
                GetString(root, "branch"),
                GetOptionalString(root, "headSha"),
                GetString(root, "workingPath")),
            ChangeTargetKind.KubernetesResource => new KubernetesResourceTarget(
                GetString(root, "clusterContext"),
                GetString(root, "apiVersion"),
                GetString(root, "resourceKind"),
                GetString(root, "namespace"),
                GetString(root, "resourceName")),
            ChangeTargetKind.IacDeployment => new IacDeploymentTarget(
                GetString(root, "backend"),
                GetString(root, "deploymentName"),
                GetString(root, "modulePath"),
                GetString(root, "environment")),
            _ => throw new JsonException(
                $"ChangeTarget kind '{kind}' has no registered deserialization mapping.")
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ChangeTarget value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(KindProperty, value.Kind.ToString());

        switch (value)
        {
            case GitRepoTarget git:
                writer.WriteString("repoUrl", git.RepoUrl);
                writer.WriteString("branch", git.Branch);
                if (git.HeadSha is not null)
                    writer.WriteString("headSha", git.HeadSha);
                writer.WriteString("workingPath", git.WorkingPath);
                break;
            case KubernetesResourceTarget k8s:
                writer.WriteString("clusterContext", k8s.ClusterContext);
                writer.WriteString("apiVersion", k8s.ApiVersion);
                writer.WriteString("resourceKind", k8s.ResourceKind);
                writer.WriteString("namespace", k8s.Namespace);
                writer.WriteString("resourceName", k8s.ResourceName);
                break;
            case IacDeploymentTarget iac:
                writer.WriteString("backend", iac.Backend);
                writer.WriteString("deploymentName", iac.DeploymentName);
                writer.WriteString("modulePath", iac.ModulePath);
                writer.WriteString("environment", iac.Environment);
                break;
            default:
                throw new NotSupportedException(
                    $"ChangeTarget subclass '{value.GetType().Name}' is not supported by the durable " +
                    "governance-state store. Extend ChangeTargetJsonConverter to persist custom targets.");
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Reads a required string property, throwing when it is absent.
    /// </summary>
    /// <remarks>
    /// Presence is mandatory even when the value may legitimately be empty. Defaulting an
    /// absent property to <see cref="string.Empty"/> would silently <em>widen</em> an approved
    /// proposal's scope on rehydration: per <c>GitRepoTarget</c>'s own contract an empty
    /// <c>WorkingPath</c> means the diff may touch any path in the repository. A truncated
    /// payload must fail loudly and quarantine its row, not resurrect as a less restricted
    /// version of what a human approved.
    /// </remarks>
    /// <param name="root">The target JSON object.</param>
    /// <param name="name">The property name.</param>
    /// <exception cref="JsonException">The property is absent or is not a string.</exception>
    private static string GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(
                $"ChangeTarget JSON is missing the required '{name}' property; refusing to " +
                "reconstruct a target from a truncated payload.");
        }

        return element.GetString() ?? string.Empty;
    }

    /// <summary>
    /// Reads a genuinely optional string property whose absence is meaningful and safe.
    /// </summary>
    /// <remarks>
    /// Only used for properties that are written when — and only when — they are non-null, so
    /// absence round-trips faithfully rather than dropping a constraint. An explicit JSON
    /// <c>null</c> is also accepted as "not set".
    /// </remarks>
    /// <param name="root">The target JSON object.</param>
    /// <param name="name">The property name.</param>
    private static string? GetOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
