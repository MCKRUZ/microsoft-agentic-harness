namespace Domain.Common.Config.AI.GitOps;

/// <summary>
/// Configuration for the GitOps skill pack (PR-9). Bound from
/// <c>AppConfig:AI:GitOps</c> in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Off by default — the skill pack is inert until <see cref="Enabled"/> is set
/// to true. When enabled the startup validator additionally requires:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="ActiveController"/> is one of <c>"flux"</c> or <c>"argocd"</c>.</description></item>
///   <item><description><see cref="K8sGptMcpServerName"/> resolves to a configured entry under <c>AppConfig.AI.McpServers.Servers</c>.</description></item>
///   <item><description>The Flux or Argo CD <see cref="FluxApiBaseUrl"/> / <see cref="ArgoCdApiBaseUrl"/> for the active controller is non-empty and a valid http(s) URL.</description></item>
/// </list>
/// <para>
/// Misconfiguration surfaces at boot via <c>GitOpsStartupValidator</c> — per
/// the plan's "no graceful degradation" rule for K8sGPT (and by the same
/// reasoning, no graceful degradation for the active controller).
/// </para>
/// </remarks>
public sealed class GitOpsConfig
{
    /// <summary>
    /// Master toggle. When false, the skill pack and all GitOps tools are
    /// inert. Default false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The active GitOps controller — the keyed-DI key under which the
    /// resolved <c>IGitOpsController</c> lives. One of <c>"flux"</c> or
    /// <c>"argocd"</c> (lowercase). The startup validator refuses to boot if
    /// this is empty or unknown when <see cref="Enabled"/> is true.
    /// </summary>
    public string ActiveController { get; set; } = string.Empty;

    /// <summary>
    /// The name of the K8sGPT MCP server entry under
    /// <c>AppConfig.AI.McpServers.Servers</c>. Default <c>"k8sgpt"</c>; the
    /// startup validator refuses to boot when this key is missing from the
    /// MCP server registry. K8sGPT is a required dependency of the
    /// <c>gitops-cluster-debug</c> skill — gracefully degraded would ship a
    /// half-working skill.
    /// </summary>
    public string K8sGptMcpServerName { get; set; } = "k8sgpt";

    /// <summary>
    /// Base URL for the Flux source-controller HTTP-exposed metrics / status
    /// surface. Used only when <see cref="ActiveController"/> is <c>"flux"</c>.
    /// </summary>
    public string FluxApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Base URL for the Argo CD API server (e.g. <c>https://argocd.example.com</c>).
    /// Used only when <see cref="ActiveController"/> is <c>"argocd"</c>.
    /// </summary>
    public string ArgoCdApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The Git repository URL the remediation pipeline targets. Required
    /// when <see cref="Enabled"/> is true — the <c>RemediationPlan</c> targets
    /// this repo regardless of which controller produced the drift.
    /// </summary>
    public string RemediationRepoUrl { get; set; } = string.Empty;

    /// <summary>
    /// The branch under <see cref="RemediationRepoUrl"/> the remediation
    /// pipeline writes changes to. Default <c>"main"</c>.
    /// </summary>
    public string RemediationBranch { get; set; } = "main";
}
