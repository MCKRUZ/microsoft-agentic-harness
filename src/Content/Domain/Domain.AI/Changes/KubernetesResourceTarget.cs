using System.Text;

namespace Domain.AI.Changes;

/// <summary>
/// A <see cref="ChangeTarget"/> identifying a Kubernetes resource managed via GitOps.
/// </summary>
/// <remarks>
/// <para>
/// The <c>MergeGate</c> for this target type never runs <c>kubectl apply</c> directly —
/// it writes the changed manifest into the GitOps repo (Argo / Flux) and lets the
/// GitOps controller reconcile the cluster. This keeps the cluster state declarative
/// and auditable through the GitOps repo history rather than through ad-hoc agent
/// API calls.
/// </para>
/// <para>
/// The four addressing fields (<see cref="ApiVersion"/>, <see cref="ResourceKind"/>,
/// <see cref="Namespace"/>, <see cref="ResourceName"/>) together uniquely identify the
/// resource within a cluster context. <see cref="ClusterContext"/> identifies which
/// cluster (matching the GitOps repo's overlay).
/// </para>
/// </remarks>
public sealed class KubernetesResourceTarget : ChangeTarget
{
    /// <summary>
    /// Construct a <see cref="KubernetesResourceTarget"/>.
    /// </summary>
    /// <param name="clusterContext">The cluster name or GitOps overlay this resource lives in (e.g. <c>prod-eastus</c>).</param>
    /// <param name="apiVersion">The Kubernetes API version (e.g. <c>apps/v1</c>, <c>v1</c>).</param>
    /// <param name="resourceKind">The resource kind (e.g. <c>Deployment</c>, <c>Service</c>, <c>ConfigMap</c>).</param>
    /// <param name="namespace">The namespace the resource lives in. Empty for cluster-scoped resources.</param>
    /// <param name="resourceName">The resource name.</param>
    public KubernetesResourceTarget(
        string clusterContext,
        string apiVersion,
        string resourceKind,
        string @namespace,
        string resourceName)
        : base(ChangeTargetKind.KubernetesResource, BuildDisplayName(clusterContext, resourceKind, @namespace, resourceName))
    {
        ClusterContext = clusterContext ?? string.Empty;
        ApiVersion = apiVersion ?? string.Empty;
        ResourceKind = resourceKind ?? string.Empty;
        Namespace = @namespace ?? string.Empty;
        ResourceName = resourceName ?? string.Empty;
    }

    /// <summary>The cluster name or GitOps overlay the resource lives in.</summary>
    public string ClusterContext { get; }

    /// <summary>The Kubernetes API version, e.g. <c>apps/v1</c>.</summary>
    public string ApiVersion { get; }

    /// <summary>The resource kind, e.g. <c>Deployment</c>.</summary>
    public string ResourceKind { get; }

    /// <summary>The namespace the resource lives in. Empty for cluster-scoped resources.</summary>
    public string Namespace { get; }

    /// <summary>The resource name.</summary>
    public string ResourceName { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Canonical form: <c>k8s:{len}:{clusterContext}/{len}:{apiVersion}/{len}:{namespace}/{len}:{resourceKind}/{len}:{resourceName}</c>,
    /// where each <c>{len}:</c> prefix is the UTF-16 length of the field that follows.
    /// </para>
    /// <para>
    /// Every addressing field is length-prefixed before being joined. The fields are
    /// free-form and several of them legally contain the <c>/</c> separator — most
    /// notably <see cref="ApiVersion"/> (e.g. <c>apps/v1</c>) and <see cref="ClusterContext"/>
    /// (GitOps overlay paths). Without the length prefix, a positional join is ambiguous:
    /// <c>(clusterContext: "prod", apiVersion: "apps/v1")</c> and
    /// <c>(clusterContext: "prod/apps", apiVersion: "v1")</c> would produce the same key,
    /// causing two genuinely different targets to collide in the deterministic proposal-id
    /// hash and silently dedupe one of the proposals away. The length prefix makes the
    /// boundary between fields unambiguous regardless of the characters inside them, so
    /// the contract on <see cref="ChangeTarget.CanonicalKey"/> ("two targets that mean
    /// different things must not collide") holds. This mirrors the length-prefixing
    /// already applied to diff edits in <c>ChangeProposalIdDeriver</c>.
    /// </para>
    /// <para>
    /// An empty <see cref="Namespace"/> (cluster-scoped resources) is encoded as a
    /// zero-length field (<c>0:</c>), which is distinct from a namespace literally named
    /// <c>cluster</c> — the old sentinel-string approach collided those two cases.
    /// </para>
    /// </remarks>
    public override string CanonicalKey()
    {
        var sb = new StringBuilder("k8s:");
        AppendField(sb, ClusterContext);
        sb.Append('/');
        AppendField(sb, ApiVersion);
        sb.Append('/');
        AppendField(sb, Namespace);
        sb.Append('/');
        AppendField(sb, ResourceKind);
        sb.Append('/');
        AppendField(sb, ResourceName);
        return sb.ToString();
    }

    /// <summary>
    /// Append a single addressing field to the canonical key as <c>{length}:{value}</c>.
    /// The length prefix guarantees the field boundary is unambiguous even when the
    /// value contains the <c>/</c> separator used to join fields.
    /// </summary>
    private static void AppendField(StringBuilder sb, string value)
    {
        sb.Append(value.Length).Append(':').Append(value);
    }

    private static string BuildDisplayName(string clusterContext, string resourceKind, string @namespace, string resourceName)
    {
        if (string.IsNullOrEmpty(resourceKind) || string.IsNullOrEmpty(resourceName))
        {
            return "(unspecified kubernetes target)";
        }

        var prefix = string.IsNullOrEmpty(clusterContext) ? "?" : clusterContext;
        var scope = string.IsNullOrEmpty(@namespace) ? "cluster" : @namespace;
        return $"{prefix}/{scope}/{resourceKind}/{resourceName}";
    }
}
