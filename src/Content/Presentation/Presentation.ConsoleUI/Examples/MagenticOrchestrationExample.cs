using Application.AI.Common.Interfaces.Orchestration.Magentic;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// Demonstrates the PR-6 Magentic orchestration surface. Builds a workflow with
/// a manager agent and two participants, runs it through
/// <see cref="IMagenticOrchestrator"/>, and prints the derived round/reset
/// counts + completion reason.
/// </summary>
/// <remarks>
/// <para>
/// This example assumes the consumer has supplied real
/// <c>Microsoft.Agents.AI.AIAgent</c> implementations (e.g.
/// <c>ChatClientAgent</c>) for the manager and participants and registered them
/// with the DI container. The example resolves those by service key — in a
/// blank harness they will not be present, in which case the example logs a
/// directive message and exits cleanly.
/// </para>
/// <para>
/// HITL plan-review pauses route through <c>IEscalationService</c> automatically
/// (wired by <c>RegisterMagenticServices</c> in
/// <c>Infrastructure.AI.DependencyInjection.Magentic</c>). State-change replans
/// route through PR-2's <c>SubmitChangeProposalCommand</c>. Neither happens in
/// this example because the demo agents are not configured to propose state
/// changes — turn on <see cref="MagenticWorkflowRequest.RequirePlanSignoff"/>
/// and feed a state-change verb into the manager's prompt to exercise either
/// path.
/// </para>
/// </remarks>
public sealed class MagenticOrchestrationExample
{
    private readonly IMagenticOrchestrator _orchestrator;
    private readonly IServiceProvider _services;
    private readonly ILogger<MagenticOrchestrationExample> _logger;

    /// <summary>Creates a new example.</summary>
    public MagenticOrchestrationExample(
        IMagenticOrchestrator orchestrator,
        IServiceProvider services,
        ILogger<MagenticOrchestrationExample> logger)
    {
        _orchestrator = orchestrator;
        _services = services;
        _logger = logger;
    }

    /// <summary>Runs the demo.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        ConsoleHelper.DisplayHeader("Magentic Orchestration (PR-6)", Color.Magenta1);

        var manager = _services.GetService(typeof(Microsoft.Agents.AI.AIAgent)) as Microsoft.Agents.AI.AIAgent;
        if (manager is null)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No AIAgent registered in DI. Register a manager + participant agents (e.g., ChatClientAgent) and re-run this example.[/]");
            return;
        }

        // Two participants reuse the same manager binding in this demo — replace
        // with role-specialized agents in production.
        var participants = new[] { manager, manager };

        var request = new MagenticWorkflowRequest
        {
            Manager = manager,
            Participants = participants,
            Task = "Summarize the high points of the latest harness changelog.",
            Name = "demo-summarize",
            RequirePlanSignoff = false,
            MaxRounds = 5,
            MaxStalls = 2
        };

        var result = await _orchestrator.RunAsync(request, ct);

        if (!result.IsSuccess)
        {
            AnsiConsole.MarkupLine($"[red]Workflow failed: {result.Errors.FirstOrDefault()}[/]");
            return;
        }

        var workflow = result.Value!;
        AnsiConsole.MarkupLine($"[green]Workflow {workflow.WorkflowName} → {workflow.CompletionReason}[/]");
        AnsiConsole.MarkupLine($"  rounds={workflow.RoundsExecuted}, resets={workflow.ResetsExecuted}, plan_reviews={workflow.PlanReviewsExecuted}");
        if (!string.IsNullOrEmpty(workflow.FinalOutput))
        {
            AnsiConsole.MarkupLine("[grey]Final output:[/]");
            AnsiConsole.WriteLine(workflow.FinalOutput);
        }

        _logger.LogInformation("Magentic demo finished: {Reason}", workflow.CompletionReason);
    }
}
