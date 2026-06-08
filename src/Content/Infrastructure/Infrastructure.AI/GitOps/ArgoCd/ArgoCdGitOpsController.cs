using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.GitOps;
using Domain.AI.Changes;
using Domain.AI.GitOps;
using Domain.AI.SkillTraining;
using Domain.Common;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.GitOps.ArgoCd;

/// <summary>
/// Argo CD implementation of <see cref="IGitOpsController"/>. Reads drift and
/// health from the Argo CD API server via <see cref="ArgoCdApiClient"/>, then
/// maps the wire shape onto the controller-neutral <see cref="DriftReport"/> /
/// <see cref="ClusterHealth"/> / <see cref="RemediationPlan"/> types.
/// </summary>
/// <remarks>
/// <para>
/// Read-only — never mutates the cluster. Remediation surfaces as a
/// <see cref="RemediationPlan"/> the <c>IGitOpsRemediationDispatcher</c>
/// translates into a <c>ChangeProposal</c> against a <c>GitRepoTarget</c>.
/// </para>
/// <para>
/// Drift detection signal: an Argo CD <c>Application</c> is considered drifted
/// when <c>Status.Sync.Status</c> is <c>OutOfSync</c> OR when
/// <c>Status.Health.Status</c> is <c>Degraded</c> / <c>Missing</c>. The
/// <c>Suspended</c> health status is surfaced as a Note in
/// <see cref="ClusterHealth"/> but NOT as drift — operators may suspend
/// reconciliation deliberately.
/// </para>
/// </remarks>
public sealed class ArgoCdGitOpsController : IGitOpsController
{
    private readonly ArgoCdApiClient _apiClient;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ArgoCdGitOpsController> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new <see cref="ArgoCdGitOpsController"/>.</summary>
    public ArgoCdGitOpsController(
        ArgoCdApiClient apiClient,
        IOptionsMonitor<AppConfig> config,
        ILogger<ArgoCdGitOpsController> logger,
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
    public GitOpsControllerKind Kind => GitOpsControllerKind.ArgoCd;

    /// <inheritdoc />
    public async Task<Result<DriftReport>> DetectDriftAsync(CancellationToken cancellationToken)
    {
        try
        {
            var applications = await _apiClient.ListApplicationsAsync(cancellationToken).ConfigureAwait(false);

            var drifted = new List<DriftedResource>();
            foreach (var app in applications)
            {
                if (IsApplicationDrifted(app))
                {
                    drifted.Add(MapDriftedApplication(app));
                }
            }

            var capturedAt = _timeProvider.GetUtcNow();
            return Result<DriftReport>.Success(new DriftReport
            {
                ControllerKind = Kind,
                CapturedAt = capturedAt,
                DriftedResources = drifted,
                EvidenceHash = ComputeEvidenceHash(applications, capturedAt)
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Argo CD drift detection failed: API unreachable.");
            return Result<DriftReport>.Fail("gitops.argocd.api_unreachable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Argo CD drift detection failed with unexpected error.");
            return Result<DriftReport>.Fail("gitops.argocd.unexpected_error");
        }
    }

    /// <inheritdoc />
    public async Task<Result<ClusterHealth>> GetClusterHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var applications = await _apiClient.ListApplicationsAsync(cancellationToken).ConfigureAwait(false);

            var resources = new List<ResourceHealth>();
            var notes = new List<string>();
            var overall = ClusterHealthStatus.Healthy;

            foreach (var app in applications)
            {
                var status = MapApplicationHealth(app);
                resources.Add(new ResourceHealth
                {
                    ApiVersion = "argoproj.io/v1alpha1",
                    Kind = "Application",
                    Namespace = app.Metadata.Namespace,
                    Name = app.Metadata.Name,
                    Status = status,
                    Message = app.Status.Health.Message
                });
                if (string.Equals(app.Status.Health.Status, "Suspended", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"Application {app.Metadata.Namespace}/{app.Metadata.Name} is suspended.");
                }
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
            _logger.LogWarning(ex, "Argo CD cluster health read failed: API unreachable.");
            return Result<ClusterHealth>.Fail("gitops.argocd.api_unreachable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Argo CD cluster health read failed with unexpected error.");
            return Result<ClusterHealth>.Fail("gitops.argocd.unexpected_error");
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
            Summary = $"Argo CD: reconverge {drift.DriftedResources.Count} drifted resource(s)."
        }));
    }

    private static bool IsApplicationDrifted(ArgoCdApplicationStatus app)
    {
        var sync = app.Status.Sync.Status;
        var health = app.Status.Health.Status;

        if (string.Equals(sync, "OutOfSync", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(health, "Degraded", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(health, "Missing", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static DriftedResource MapDriftedApplication(ArgoCdApplicationStatus app)
    {
        var sev = string.Equals(app.Status.Health.Status, "Degraded", StringComparison.OrdinalIgnoreCase)
            ? DriftSeverity.High
            : string.Equals(app.Status.Health.Status, "Missing", StringComparison.OrdinalIgnoreCase)
                ? DriftSeverity.High
                : DriftSeverity.Medium;

        var summary = string.IsNullOrEmpty(app.Status.Health.Message)
            ? $"Sync={app.Status.Sync.Status}, Health={app.Status.Health.Status}"
            : app.Status.Health.Message;

        return new DriftedResource
        {
            ApiVersion = "argoproj.io/v1alpha1",
            Kind = "Application",
            Namespace = app.Metadata.Namespace,
            Name = app.Metadata.Name,
            DesiredPath = app.Spec.Source.Path,
            Summary = summary,
            Severity = sev
        };
    }

    private static ClusterHealthStatus MapApplicationHealth(ArgoCdApplicationStatus app)
    {
        var sync = app.Status.Sync.Status;
        var health = app.Status.Health.Status;

        if (string.Equals(health, "Healthy", StringComparison.OrdinalIgnoreCase)
            && string.Equals(sync, "Synced", StringComparison.OrdinalIgnoreCase))
            return ClusterHealthStatus.Healthy;
        if (string.Equals(health, "Progressing", StringComparison.OrdinalIgnoreCase))
            return ClusterHealthStatus.Progressing;
        if (string.Equals(health, "Suspended", StringComparison.OrdinalIgnoreCase))
            return ClusterHealthStatus.Progressing;
        if (string.Equals(health, "Degraded", StringComparison.OrdinalIgnoreCase))
            return ClusterHealthStatus.Degraded;
        if (string.Equals(health, "Missing", StringComparison.OrdinalIgnoreCase))
            return ClusterHealthStatus.Failed;
        return ClusterHealthStatus.Unknown;
    }

    private static ClusterHealthStatus Worst(ClusterHealthStatus a, ClusterHealthStatus b) =>
        (ClusterHealthStatus)Math.Max((int)a, (int)b);

    private static string BuildReconvergePlaceholder(DriftedResource r) =>
        $"# reconverge target for {r.Kind} {r.Namespace}/{r.Name} — operator action required";

    private static string ComputeEvidenceHash(
        IReadOnlyList<ArgoCdApplicationStatus> applications,
        DateTimeOffset capturedAt)
    {
        var sb = new StringBuilder();
        sb.Append("argocd|").Append(capturedAt.ToUnixTimeSeconds()).Append('|');
        foreach (var app in applications.OrderBy(a => a.Metadata.Namespace, StringComparer.Ordinal).ThenBy(a => a.Metadata.Name, StringComparer.Ordinal))
            sb.Append("a:").Append(app.Metadata.Namespace).Append('/').Append(app.Metadata.Name).Append(':')
              .Append(app.Status.Sync.Status).Append(':').Append(app.Status.Health.Status).Append(';');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }
}
