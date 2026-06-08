using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.GitOps;
using Domain.AI.Changes;
using Domain.AI.GitOps;
using Domain.Common;
using Domain.Common.Config;
using Domain.AI.SkillTraining;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.GitOps.Flux;

/// <summary>
/// Flux v2 implementation of <see cref="IGitOpsController"/>. Reads drift and
/// health from the Flux source-controller + kustomize-controller + helm-controller
/// status surface via <see cref="FluxApiClient"/>, then maps the wire shape onto
/// the controller-neutral <see cref="DriftReport"/> / <see cref="ClusterHealth"/>
/// / <see cref="RemediationPlan"/> types.
/// </summary>
/// <remarks>
/// <para>
/// Read-only — never mutates the cluster. Remediation surfaces as a
/// <see cref="RemediationPlan"/> the <c>IGitOpsRemediationDispatcher</c>
/// translates into a <c>ChangeProposal</c> against a <c>GitRepoTarget</c>.
/// </para>
/// <para>
/// Drift detection signal: a <c>Kustomization</c> or <c>HelmRelease</c> is
/// considered drifted when either (a) the controller reports it as not Ready
/// and not Suspended, or (b) <c>LastAppliedRevision</c> differs from
/// <c>LastAttemptedRevision</c>. Suspended resources are surfaced in
/// <see cref="ClusterHealth"/> as Notes but NOT as drift — operators may
/// suspend reconciliation deliberately.
/// </para>
/// </remarks>
public sealed class FluxGitOpsController : IGitOpsController
{
    private readonly FluxApiClient _apiClient;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<FluxGitOpsController> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="FluxGitOpsController"/>.</summary>
    public FluxGitOpsController(
        FluxApiClient apiClient,
        IOptionsMonitor<AppConfig> config,
        ILogger<FluxGitOpsController> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _apiClient = apiClient;
        _config = config;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public GitOpsControllerKind Kind => GitOpsControllerKind.Flux;

    /// <inheritdoc />
    public async Task<Result<DriftReport>> DetectDriftAsync(CancellationToken cancellationToken)
    {
        try
        {
            var kustomizations = await _apiClient.ListKustomizationsAsync(cancellationToken).ConfigureAwait(false);
            var helmReleases = await _apiClient.ListHelmReleasesAsync(cancellationToken).ConfigureAwait(false);

            var drifted = new List<DriftedResource>();
            foreach (var k in kustomizations)
            {
                if (IsKustomizationDrifted(k))
                {
                    drifted.Add(MapKustomization(k));
                }
            }
            foreach (var h in helmReleases)
            {
                if (IsHelmReleaseDrifted(h))
                {
                    drifted.Add(MapHelmRelease(h));
                }
            }

            var capturedAt = _timeProvider.GetUtcNow();
            var evidenceHash = ComputeEvidenceHash(kustomizations, helmReleases, capturedAt);

            var report = new DriftReport
            {
                ControllerKind = Kind,
                CapturedAt = capturedAt,
                DriftedResources = drifted,
                EvidenceHash = evidenceHash
            };

            return Result<DriftReport>.Success(report);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Flux drift detection failed: API unreachable.");
            return Result<DriftReport>.Fail("gitops.flux.api_unreachable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Flux drift detection failed with unexpected error.");
            return Result<DriftReport>.Fail("gitops.flux.unexpected_error");
        }
    }

    /// <inheritdoc />
    public async Task<Result<ClusterHealth>> GetClusterHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var kustomizations = await _apiClient.ListKustomizationsAsync(cancellationToken).ConfigureAwait(false);
            var helmReleases = await _apiClient.ListHelmReleasesAsync(cancellationToken).ConfigureAwait(false);

            var resources = new List<ResourceHealth>();
            var notes = new List<string>();
            var overall = ClusterHealthStatus.Healthy;

            foreach (var k in kustomizations)
            {
                var status = MapKustomizationStatus(k);
                resources.Add(new ResourceHealth
                {
                    ApiVersion = "kustomize.toolkit.fluxcd.io/v1",
                    Kind = "Kustomization",
                    Namespace = k.Namespace,
                    Name = k.Name,
                    Status = status,
                    Message = k.Message
                });
                if (k.Suspended) notes.Add($"Kustomization {k.Namespace}/{k.Name} is suspended.");
                overall = Worst(overall, status);
            }
            foreach (var h in helmReleases)
            {
                var status = MapHelmReleaseStatus(h);
                resources.Add(new ResourceHealth
                {
                    ApiVersion = "helm.toolkit.fluxcd.io/v2",
                    Kind = "HelmRelease",
                    Namespace = h.Namespace,
                    Name = h.Name,
                    Status = status,
                    Message = h.Message
                });
                if (h.Suspended) notes.Add($"HelmRelease {h.Namespace}/{h.Name} is suspended.");
                overall = Worst(overall, status);
            }

            return Result<ClusterHealth>.Success(new ClusterHealth
            {
                ControllerKind = Kind,
                CapturedAt = _timeProvider.GetUtcNow(),
                OverallStatus = overall,
                ResourceStates = resources,
                Notes = notes
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Flux cluster health read failed: API unreachable.");
            return Result<ClusterHealth>.Fail("gitops.flux.api_unreachable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Flux cluster health read failed with unexpected error.");
            return Result<ClusterHealth>.Fail("gitops.flux.unexpected_error");
        }
    }

    /// <inheritdoc />
    public Task<Result<RemediationPlan>> ProposeRemediationAsync(DriftReport drift, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drift);
        if (drift.ControllerKind != Kind)
        {
            return Task.FromResult(Result<RemediationPlan>.Fail("gitops.remediation.controller_mismatch"));
        }
        if (!drift.HasDrift)
        {
            return Task.FromResult(Result<RemediationPlan>.Fail("gitops.remediation.no_drift"));
        }

        var gitops = _config.CurrentValue.AI.GitOps;
        if (string.IsNullOrWhiteSpace(gitops.RemediationRepoUrl))
        {
            return Task.FromResult(Result<RemediationPlan>.Fail("gitops.remediation.no_repo_configured"));
        }

        var target = new GitRepoTarget(
            repoUrl: gitops.RemediationRepoUrl,
            branch: gitops.RemediationBranch,
            headSha: null,
            workingPath: string.Empty);

        var edits = drift.DriftedResources
            .Where(r => !string.IsNullOrEmpty(r.DesiredPath))
            .Select(r => new ChangeEdit
            {
                Op = EditOp.Replace,
                Target = r.DesiredPath!,
                Content = BuildReconvergePlaceholder(r)
            })
            .ToList();

        if (edits.Count == 0)
        {
            return Task.FromResult(Result<RemediationPlan>.Fail("gitops.remediation.no_remediable_drift"));
        }

        var maxSeverity = drift.DriftedResources.Max(r => r.Severity);
        var blastRadius = maxSeverity switch
        {
            DriftSeverity.High => BlastRadius.High,
            DriftSeverity.Medium => BlastRadius.Medium,
            _ => BlastRadius.Low
        };

        return Task.FromResult(Result<RemediationPlan>.Success(new RemediationPlan
        {
            ControllerKind = Kind,
            SourceDrift = drift,
            Target = target,
            Edits = edits,
            ProposedBlastRadius = blastRadius,
            Summary = $"Flux: reconverge {drift.DriftedResources.Count} drifted resource(s)."
        }));
    }

    private static bool IsKustomizationDrifted(FluxKustomizationStatus k)
    {
        if (k.Suspended) return false;
        if (!k.Ready) return true;
        return !string.Equals(k.LastAppliedRevision, k.LastAttemptedRevision, StringComparison.Ordinal)
               && !string.IsNullOrEmpty(k.LastAttemptedRevision);
    }

    private static bool IsHelmReleaseDrifted(FluxHelmReleaseStatus h)
    {
        if (h.Suspended) return false;
        return !h.Ready;
    }

    private static DriftedResource MapKustomization(FluxKustomizationStatus k) => new()
    {
        ApiVersion = "kustomize.toolkit.fluxcd.io/v1",
        Kind = "Kustomization",
        Namespace = k.Namespace,
        Name = k.Name,
        DesiredPath = k.Path,
        Summary = string.IsNullOrEmpty(k.Message) ? "Kustomization not Ready." : k.Message,
        Severity = DriftSeverity.Medium
    };

    private static DriftedResource MapHelmRelease(FluxHelmReleaseStatus h) => new()
    {
        ApiVersion = "helm.toolkit.fluxcd.io/v2",
        Kind = "HelmRelease",
        Namespace = h.Namespace,
        Name = h.Name,
        DesiredPath = null,
        Summary = string.IsNullOrEmpty(h.Message) ? "HelmRelease not Ready." : h.Message,
        Severity = DriftSeverity.High
    };

    private static ClusterHealthStatus MapKustomizationStatus(FluxKustomizationStatus k)
    {
        if (k.Suspended) return ClusterHealthStatus.Progressing;
        if (k.Ready) return ClusterHealthStatus.Healthy;
        return ClusterHealthStatus.Degraded;
    }

    private static ClusterHealthStatus MapHelmReleaseStatus(FluxHelmReleaseStatus h)
    {
        if (h.Suspended) return ClusterHealthStatus.Progressing;
        if (h.Ready) return ClusterHealthStatus.Healthy;
        return ClusterHealthStatus.Failed;
    }

    private static ClusterHealthStatus Worst(ClusterHealthStatus a, ClusterHealthStatus b) =>
        (ClusterHealthStatus)Math.Max((int)a, (int)b);

    private static string BuildReconvergePlaceholder(DriftedResource r) =>
        $"# reconverge target for {r.Kind} {r.Namespace}/{r.Name} — operator action required";

    private static string ComputeEvidenceHash(
        IReadOnlyList<FluxKustomizationStatus> kustomizations,
        IReadOnlyList<FluxHelmReleaseStatus> helmReleases,
        DateTimeOffset capturedAt)
    {
        var sb = new StringBuilder();
        sb.Append("flux|").Append(capturedAt.ToUnixTimeSeconds()).Append('|');
        foreach (var k in kustomizations.OrderBy(k => k.Namespace, StringComparer.Ordinal).ThenBy(k => k.Name, StringComparer.Ordinal))
            sb.Append("k:").Append(k.Namespace).Append('/').Append(k.Name).Append(':').Append(k.Ready).Append(':').Append(k.LastAppliedRevision).Append(';');
        foreach (var h in helmReleases.OrderBy(h => h.Namespace, StringComparer.Ordinal).ThenBy(h => h.Name, StringComparer.Ordinal))
            sb.Append("h:").Append(h.Namespace).Append('/').Append(h.Name).Append(':').Append(h.Ready).Append(';');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}
