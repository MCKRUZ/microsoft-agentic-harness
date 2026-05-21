using Application.AI.Common.Interfaces.Escalation;
using Domain.AI.Escalation;
using Domain.AI.Governance;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates multi-approval escalation workflows with AnyOf, AllOf, and Quorum strategies.
/// Shows approval creation, decision submission, cancellation, and pending escalation querying.
/// </summary>
public class EscalationApprovalsExample
{
    private readonly IEscalationService _escalationService;
    private readonly ILogger<EscalationApprovalsExample> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EscalationApprovalsExample"/> class.
    /// </summary>
    /// <param name="escalationService">Escalation service for managing approval workflows.</param>
    /// <param name="logger">Logger instance.</param>
    public EscalationApprovalsExample(
        IEscalationService escalationService,
        ILogger<EscalationApprovalsExample> logger)
    {
        _escalationService = escalationService;
        _logger = logger;
    }

    /// <summary>
    /// Runs the escalation approval example with 5 demonstration steps.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ConsoleHelper.DisplayHeader("Governance: Escalation & Approvals", Color.Cyan);
            ConsoleHelper.DisplayModeInfo(isLive: false, "In-memory escalation state");
            AnsiConsole.WriteLine();

            await Step1_AnyOfApprovalAsync(cancellationToken);
            await Step2_AllOfApprovalAsync(cancellationToken);
            await Step3_QuorumApprovalAsync(cancellationToken);
            await Step4_CancellationAsync(cancellationToken);
            await Step5_ListPendingAsync(cancellationToken);

            ConsoleHelper.DisplaySuccess("All escalation approval demonstrations completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during escalation approval example");
            ConsoleHelper.DisplayError($"Example failed: {ex.Message}");
        }
    }

    private async Task Step1_AnyOfApprovalAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(1, 5, "AnyOf Approval (First Approver Wins)");

        var request = CreateRequest(
            "Delete sensitive cache (AnyOf strategy)",
            ApprovalStrategyType.AnyOf,
            ["alice", "bob"],
            0);

        var escalationId = await _escalationService.QueueEscalationAsync(request, cancellationToken);
        ConsoleHelper.DisplayInfo("Escalation Queued", $"ID: {escalationId}");

        // Alice approves
        var aliceDecision = new ApproverDecision
        {
            ApproverName = "alice",
            Approved = true,
            Reason = "Looks good, cache is safe to clear.",
            RespondedAt = DateTimeOffset.UtcNow
        };

        var outcome = await _escalationService.SubmitDecisionAsync(escalationId, aliceDecision, cancellationToken);
        if (outcome != null)
        {
            DisplayOutcome("AnyOf Result", outcome);
            AnsiConsole.WriteLine($"[bold]Escalation Resolved After:[/] {outcome.Decisions.Count} approver decision(s)");
        }

        AnsiConsole.WriteLine();
        await Task.CompletedTask;
    }

    private async Task Step2_AllOfApprovalAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(2, 5, "AllOf Approval (All Must Approve)");

        var request = CreateRequest(
            "Grant elevated API permissions",
            ApprovalStrategyType.AllOf,
            ["alice", "bob"],
            0);

        var escalationId = await _escalationService.QueueEscalationAsync(request, cancellationToken);
        ConsoleHelper.DisplayInfo("Escalation Queued", $"ID: {escalationId}");

        // Alice approves
        var aliceDecision = new ApproverDecision
        {
            ApproverName = "alice",
            Approved = true,
            Reason = "Request justified by operational need.",
            RespondedAt = DateTimeOffset.UtcNow
        };

        var outcome1 = await _escalationService.SubmitDecisionAsync(escalationId, aliceDecision, cancellationToken);
        AnsiConsole.WriteLine($"[yellow]After Alice approved: {(outcome1 == null ? "Still pending (Bob must approve)" : "Resolved")}[/]");

        // Bob approves
        var bobDecision = new ApproverDecision
        {
            ApproverName = "bob",
            Approved = true,
            Reason = "Security review passed.",
            RespondedAt = DateTimeOffset.UtcNow
        };

        var outcome2 = await _escalationService.SubmitDecisionAsync(escalationId, bobDecision, cancellationToken);
        if (outcome2 != null)
        {
            DisplayOutcome("AllOf Result", outcome2);
            AnsiConsole.WriteLine($"[bold]Escalation Resolved After:[/] All {outcome2.Decisions.Count} approvers decided");
        }

        AnsiConsole.WriteLine();
        await Task.CompletedTask;
    }

    private async Task Step3_QuorumApprovalAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(3, 5, "Quorum Approval (2-of-3)");

        var request = CreateRequest(
            "Publish critical security patch",
            ApprovalStrategyType.Quorum,
            ["alice", "bob", "charlie"],
            2);  // 2-of-3 threshold

        var escalationId = await _escalationService.QueueEscalationAsync(request, cancellationToken);
        ConsoleHelper.DisplayInfo("Escalation Queued", $"ID: {escalationId}");
        AnsiConsole.WriteLine($"[bold]Quorum Threshold:[/] 2 of 3 approvers");

        // Alice approves
        var aliceDecision = new ApproverDecision
        {
            ApproverName = "alice",
            Approved = true,
            Reason = "Critical patch needed immediately.",
            RespondedAt = DateTimeOffset.UtcNow
        };

        var outcome1 = await _escalationService.SubmitDecisionAsync(escalationId, aliceDecision, cancellationToken);
        AnsiConsole.WriteLine($"[yellow]After Alice: {(outcome1 == null ? "1/2 approvals received, pending" : "Quorum met")}[/]");

        // Bob approves (quorum met)
        var bobDecision = new ApproverDecision
        {
            ApproverName = "bob",
            Approved = true,
            Reason = "Verified patch contents, safe to deploy.",
            RespondedAt = DateTimeOffset.UtcNow
        };

        var outcome2 = await _escalationService.SubmitDecisionAsync(escalationId, bobDecision, cancellationToken);
        if (outcome2 != null)
        {
            DisplayOutcome("Quorum Result", outcome2);
            AnsiConsole.WriteLine($"[bold]Escalation Resolved After:[/] {outcome2.Decisions.Count} approvers (quorum met)");
        }

        AnsiConsole.WriteLine();
        await Task.CompletedTask;
    }

    private async Task Step4_CancellationAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(4, 5, "Escalation Cancellation");

        var request = CreateRequest(
            "Deploy new feature (will be cancelled)",
            ApprovalStrategyType.AnyOf,
            ["alice"],
            0);

        var escalationId = await _escalationService.QueueEscalationAsync(request, cancellationToken);
        ConsoleHelper.DisplayInfo("Escalation Queued", $"ID: {escalationId}");

        // Cancel the escalation
        AnsiConsole.WriteLine("[yellow]Cancelling escalation...[/]");
        var cancelOutcome = await _escalationService.CancelEscalationAsync(
            escalationId,
            "Feature release postponed, cancelling approval request",
            cancellationToken);

        DisplayOutcome("Cancellation Outcome", cancelOutcome);
        AnsiConsole.WriteLine($"[bold]Resolution Type:[/] {cancelOutcome.ResolutionType}");

        AnsiConsole.WriteLine();
        await Task.CompletedTask;
    }

    private async Task Step5_ListPendingAsync(CancellationToken cancellationToken)
    {
        ConsoleHelper.DisplayStep(5, 5, "List Pending Escalations for Approver");

        // Queue multiple escalations for "reviewer"
        var pending = new List<Guid>();

        for (int i = 0; i < 2; i++)
        {
            var request = CreateRequest(
                $"Pending request {i + 1}",
                ApprovalStrategyType.AnyOf,
                ["reviewer"],
                0);

            var escalationId = await _escalationService.QueueEscalationAsync(request, cancellationToken);
            pending.Add(escalationId);
            ConsoleHelper.DisplayInfo($"Request {i + 1} Queued", $"ID: {escalationId}");
        }

        AnsiConsole.WriteLine();

        // Retrieve pending escalations for "reviewer"
        var pendingEscalations = await _escalationService.GetPendingEscalationsAsync(
            "reviewer",
            cancellationToken);

        if (pendingEscalations.Count == 0)
        {
            ConsoleHelper.DisplayInfo("Pending Escalations", "None found for approver 'reviewer'");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.Title("[bold cyan]Pending Escalations for 'reviewer'[/]");
            table.AddColumn("[bold]Escalation ID[/]");
            table.AddColumn("[bold]Tool[/]");
            table.AddColumn("[bold]Description[/]");
            table.AddColumn("[bold]Risk Level[/]");
            table.AddColumn("[bold]Priority[/]");

            foreach (var esc in pendingEscalations)
            {
                table.AddRow(
                    $"[cyan]{esc.EscalationId.ToString()[..8]}...[/]",
                    Markup.Escape(esc.ToolName),
                    Markup.Escape(esc.Description),
                    esc.RiskLevel.ToString(),
                    esc.Priority.ToString());
            }

            AnsiConsole.Write(table);
        }

        AnsiConsole.WriteLine();
        await Task.CompletedTask;
    }

    private EscalationRequest CreateRequest(
        string description,
        ApprovalStrategyType strategy,
        IReadOnlyList<string> approvers,
        int quorumThreshold)
    {
        return new EscalationRequest
        {
            EscalationId = Guid.NewGuid(),
            AgentId = "demo-agent",
            ToolName = "sensitive-operation",
            Arguments = new Dictionary<string, string>
            {
                { "action", "execute" },
                { "resource", "system-critical" }
            },
            Description = description,
            RiskLevel = RiskLevel.High,
            Priority = EscalationPriority.Blocking,
            ApprovalStrategy = strategy,
            Approvers = approvers,
            QuorumThreshold = quorumThreshold,
            TimeoutSeconds = 300,
            TimeoutAction = EscalationTimeoutAction.DenyAndEscalate,
            RequestedAt = DateTimeOffset.UtcNow
        };
    }

    private void DisplayOutcome(string label, EscalationOutcome outcome)
    {
        var statusColor = outcome.IsApproved ? "green" : "red";
        var statusText = outcome.IsApproved ? "APPROVED" : "DENIED";

        AnsiConsole.WriteLine($"[bold {statusColor}]{label}:[/] {statusText}");
        AnsiConsole.WriteLine($"[bold]Resolution Type:[/] {outcome.ResolutionType}");
        AnsiConsole.WriteLine($"[bold]Resolved At:[/] {outcome.ResolvedAt:O}");

        if (outcome.Decisions.Count > 0)
        {
            var decisionsTable = new Table().Border(TableBorder.Rounded);
            decisionsTable.Title("[bold]Approver Decisions[/]");
            decisionsTable.AddColumn("[bold]Approver[/]");
            decisionsTable.AddColumn("[bold]Decision[/]");
            decisionsTable.AddColumn("[bold]Reason[/]");
            decisionsTable.AddColumn("[bold]Responded At[/]");

            foreach (var decision in outcome.Decisions)
            {
                var decisionColor = decision.Approved ? "green" : "red";
                var decisionText = decision.Approved ? "Approved" : "Denied";

                decisionsTable.AddRow(
                    Markup.Escape(decision.ApproverName),
                    $"[{decisionColor}]{decisionText}[/]",
                    Markup.Escape(decision.Reason ?? "(no reason provided)"),
                    decision.RespondedAt.ToString("g"));
            }

            AnsiConsole.Write(decisionsTable);
        }
    }
}
