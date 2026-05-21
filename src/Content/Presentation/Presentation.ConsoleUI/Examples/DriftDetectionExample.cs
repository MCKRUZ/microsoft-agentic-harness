using Application.AI.Common.Interfaces.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates EWMA-based quality drift monitoring with severity classification and escalation.
/// Shows how baseline establishment, normal operation, and degradation progress through
/// severity levels (None → Warn → Alert → Escalate).
/// </summary>
public class DriftDetectionExample
{
    private readonly IDriftDetectionService _driftService;
    private readonly IDriftAuditStore _auditStore;
    private readonly ILogger<DriftDetectionExample> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DriftDetectionExample"/> class.
    /// </summary>
    /// <param name="driftService">Core drift detection service.</param>
    /// <param name="auditStore">Audit record persistence.</param>
    /// <param name="logger">Logger instance.</param>
    public DriftDetectionExample(
        IDriftDetectionService driftService,
        IDriftAuditStore auditStore,
        ILogger<DriftDetectionExample> logger)
    {
        _driftService = driftService;
        _auditStore = auditStore;
        _logger = logger;
    }

    /// <summary>
    /// Runs the drift detection example with 4 demonstration steps.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ConsoleHelper.DisplayHeader("Drift Detection: EWMA Quality Monitoring", Color.Cyan);
            ConsoleHelper.DisplayModeInfo(isLive: false, "EWMA is pure math — fully offline");
            AnsiConsole.WriteLine();

            await Step1_EstablishBaselineAsync(cancellationToken);
            await Step2_RecordNormalScoresAsync(cancellationToken);
            await Step3_RecordDegradingScoresAsync(cancellationToken);
            await Step4_DisplayAuditTrailAsync(cancellationToken);

            ConsoleHelper.DisplaySuccess("All drift detection demonstrations completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during drift detection example");
            ConsoleHelper.DisplayError($"Example failed: {ex.Message}");
        }
    }

    private async Task Step1_EstablishBaselineAsync(CancellationToken ct)
    {
        ConsoleHelper.DisplayStep(1, 4, "Establish Baseline (Quality Score ~0.85)");

        // First, record baseline-quality scores to build history
        AnsiConsole.WriteLine("Recording baseline history (quality ~0.85)...");
        var baselineScores = new[] { 0.84, 0.86, 0.85, 0.83, 0.87 };

        foreach (var score in baselineScores)
        {
            var request = new DriftEvaluationRequest
            {
                Scope = DriftScope.Agent,
                ScopeIdentifier = "agent-001",
                Dimensions = new Dictionary<DriftDimension, double>
                {
                    { DriftDimension.Faithfulness, score },
                    { DriftDimension.Relevance, score },
                    { DriftDimension.StructuralConformance, score },
                    { DriftDimension.ToolUsageAccuracy, score },
                    { DriftDimension.Coherence, score },
                    { DriftDimension.InstructionFollowing, score }
                }
            };

            await _driftService.EvaluateDriftAsync(request, ct);
        }

        // Now compute the baseline from the history
        var baselineRequest = new DriftBaselineUpdateRequest
        {
            Scope = DriftScope.Agent,
            ScopeIdentifier = "agent-001"
        };

        var baselineResult = await _driftService.UpdateBaselineAsync(baselineRequest, ct);

        if (baselineResult.IsSuccess)
        {
            var baseline = baselineResult.Value;
            AnsiConsole.WriteLine($"[green]✓[/] Baseline computed: [bold]{baseline.BaselineId}[/]");
            AnsiConsole.WriteLine($"  Scope: {baseline.Scope} / {baseline.ScopeIdentifier}");
            AnsiConsole.WriteLine($"  Samples: {baseline.SampleCount}");
            AnsiConsole.WriteLine($"  Window: {baseline.WindowStart:u} → {baseline.WindowEnd:u}");
            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.WriteLine($"[red]✗[/] Baseline creation failed: {string.Join(", ", baselineResult.Errors)}");
        }
    }

    private async Task Step2_RecordNormalScoresAsync(CancellationToken ct)
    {
        ConsoleHelper.DisplayStep(2, 4, "Record Normal Scores (0.80–0.90 range)");

        var normalScores = new[] { 0.88, 0.82, 0.87, 0.84, 0.86 };
        var scores = new List<DriftScore>();

        foreach (var score in normalScores)
        {
            var request = new DriftEvaluationRequest
            {
                Scope = DriftScope.Agent,
                ScopeIdentifier = "agent-001",
                Dimensions = new Dictionary<DriftDimension, double>
                {
                    { DriftDimension.Faithfulness, score },
                    { DriftDimension.Relevance, score },
                    { DriftDimension.StructuralConformance, score },
                    { DriftDimension.ToolUsageAccuracy, score },
                    { DriftDimension.Coherence, score },
                    { DriftDimension.InstructionFollowing, score }
                }
            };

            var result = await _driftService.EvaluateDriftAsync(request, ct);

            if (result.IsSuccess)
            {
                scores.Add(result.Value);
                AnsiConsole.WriteLine($"  Score: {score:F2} → Severity: [bold]{result.Value.Severity}[/] (Drift: {result.Value.OverallDrift:F2}σ)");
            }
        }

        AnsiConsole.WriteLine($"[green]✓[/] Recorded {scores.Count} normal evaluations. EWMA stable within bounds.");
        AnsiConsole.WriteLine();
    }

    private async Task Step3_RecordDegradingScoresAsync(CancellationToken ct)
    {
        ConsoleHelper.DisplayStep(3, 4, "Record Degrading Scores (0.75 → 0.55)");

        var degradingScores = new[] { 0.75, 0.70, 0.65, 0.60, 0.55 };
        var severityProgression = new List<(double Score, DriftSeverity Severity, double Drift)>();

        foreach (var score in degradingScores)
        {
            var request = new DriftEvaluationRequest
            {
                Scope = DriftScope.Agent,
                ScopeIdentifier = "agent-001",
                Dimensions = new Dictionary<DriftDimension, double>
                {
                    { DriftDimension.Faithfulness, score },
                    { DriftDimension.Relevance, score },
                    { DriftDimension.StructuralConformance, score },
                    { DriftDimension.ToolUsageAccuracy, score },
                    { DriftDimension.Coherence, score },
                    { DriftDimension.InstructionFollowing, score }
                }
            };

            var result = await _driftService.EvaluateDriftAsync(request, ct);

            if (result.IsSuccess)
            {
                severityProgression.Add((score, result.Value.Severity, result.Value.OverallDrift));
                var color = result.Value.Severity switch
                {
                    DriftSeverity.None => "white",
                    DriftSeverity.Warn => "yellow",
                    DriftSeverity.Alert => "orange1",
                    DriftSeverity.Escalate => "red",
                    _ => "white"
                };

                AnsiConsole.WriteLine($"  Score: {score:F2} → Severity: [{color}]{result.Value.Severity}[/] (Drift: {result.Value.OverallDrift:F2}σ)");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("[bold]Severity Progression:[/]");
        var table = new Table();
        table.AddColumn("Score");
        table.AddColumn("Severity");
        table.AddColumn("Deviation (σ)");
        table.AddColumn("Status");

        foreach (var (score, severity, drift) in severityProgression)
        {
            var status = severity switch
            {
                DriftSeverity.None => "[green]Normal[/]",
                DriftSeverity.Warn => "[yellow]Warning[/]",
                DriftSeverity.Alert => "[orange1]Alert[/]",
                DriftSeverity.Escalate => "[red]Escalate[/]",
                _ => "[white]?[/]"
            };

            table.AddRow(
                score.ToString("F2"),
                severity.ToString(),
                drift.ToString("F2"),
                status
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private async Task Step4_DisplayAuditTrailAsync(CancellationToken ct)
    {
        ConsoleHelper.DisplayStep(4, 4, "Display Audit Trail");

        var query = new DriftHistoryQuery
        {
            Scope = DriftScope.Agent,
            ScopeIdentifier = "agent-001",
            Start = DateTimeOffset.UtcNow.AddHours(-1),
            End = DateTimeOffset.UtcNow
        };

        var historyResult = await _driftService.GetDriftHistoryAsync(query, ct);

        if (historyResult.IsSuccess)
        {
            var history = historyResult.Value;
            AnsiConsole.WriteLine($"[green]✓[/] Retrieved {history.Count} historical scores.");

            if (history.Count > 0)
            {
                var auditQuery = new DriftAuditQuery
                {
                    Start = DateTimeOffset.UtcNow.AddHours(-1),
                    End = DateTimeOffset.UtcNow
                };

                var auditResult = await _auditStore.GetRecordsAsync(auditQuery, ct);

                if (auditResult.IsSuccess)
                {
                    var records = auditResult.Value;
                    AnsiConsole.WriteLine($"[green]✓[/] Retrieved {records.Count} audit entries.");

                    if (records.Count > 0)
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.WriteLine("[bold]Audit Trail:[/]");
                        var table = new Table();
                        table.AddColumn("Type");
                        table.AddColumn("Event ID");
                        table.AddColumn("Recorded At");

                        foreach (var record in records.OrderBy(r => r.RecordedAt).Take(10))
                        {
                            table.AddRow(
                                record.RecordType.ToString(),
                                record.EventId.ToString("N").Substring(0, 8),
                                record.RecordedAt.ToString("u")
                            );
                        }

                        AnsiConsole.Write(table);
                    }
                }
            }

            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.WriteLine("[red]✗[/] Failed to retrieve drift history");
        }
    }
}
