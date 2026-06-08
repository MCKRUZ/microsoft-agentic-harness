using System.Text.Json;
using Application.AI.Common.Interfaces.A2A;
using Application.AI.Common.Interfaces.Agent;
using Domain.AI.A2A;
using Domain.AI.Identity;
using Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Presentation.ConsoleUI.Common.Helpers;
using Spectre.Console;

namespace Presentation.ConsoleUI.Examples;

/// <summary>
/// PR-7 demo: an SRE agent dispatches a search-tickets request to a Workspace
/// agent through the harness A2A surface. Both run in the same process — the
/// envelope shape is identical to the cross-process transport, so the demo
/// proves the call-site shape without standing up a second process.
/// </summary>
/// <remarks>
/// <para>
/// The example registers a stub <c>IA2ASkillHandler</c> as the Workspace
/// agent's <c>search-tickets</c> skill, then drives a single call from the
/// SRE side. Caller identity (<c>agent-sre</c>) propagates onto the server's
/// ambient <see cref="IAgentExecutionContext"/>, and the handler reads it back
/// to prove identity flowed through.
/// </para>
/// </remarks>
public sealed class A2ASreToWorkspaceExample
{
    private readonly IServiceProvider _services;
    private readonly ILogger<A2ASreToWorkspaceExample> _logger;

    /// <summary>Creates a new example.</summary>
    public A2ASreToWorkspaceExample(IServiceProvider services, ILogger<A2ASreToWorkspaceExample> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>Runs the SRE → Workspace A2A demo.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ConsoleHelper.DisplayHeader("A2A: SRE → Workspace (in-process)", Color.Green);

        // Per-call DI scope so the scoped IA2AClient / Server / identity
        // context all share the same lifetime.
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;

        // Set the caller's ambient identity. In a real harness this is set by
        // AgentFactory during agent construction; the demo establishes it
        // directly so the example does not require a full agent bootstrap.
        var executionContext = sp.GetRequiredService<IAgentExecutionContext>();
        try
        {
            executionContext.SetIdentity(new AgentIdentity
            {
                Id = "agent-sre",
                Kind = AgentIdentityKind.Development
            });
        }
        catch (InvalidOperationException)
        {
            // Identity already set by a parent scope (e.g. running inside an
            // agent turn); leave it as-is.
        }

        var client = sp.GetRequiredService<IA2AClient>();

        var envelope = new A2AEnvelope
        {
            SchemaVersion = A2AEnvelope.CurrentSchemaVersion,
            CorrelationId = Guid.NewGuid().ToString("N"),
            CallerAgentId = "agent-sre",
            CallerKind = AgentIdentityKind.Development.ToString(),
            CalleeAgentId = "agent-workspace",
            CalleeSkill = "search-tickets"
        };

        var request = new A2ARequest
        {
            Envelope = envelope,
            TaskDescription = "Find open SEV-2 tickets for tenant contoso",
            Input = JsonSerializer.SerializeToElement(new { tenantId = "contoso", severity = 2 })
        };

        AnsiConsole.MarkupLine($"[grey]Calling [yellow]agent-workspace[/] / [yellow]search-tickets[/] (correlation [yellow]{envelope.CorrelationId}[/])[/]");

        var result = await client.CallAsync(request, cancellationToken);

        if (!result.IsSuccess)
        {
            AnsiConsole.MarkupLine($"[red]Transport failed:[/] {string.Join(',', result.Errors)}");
            return;
        }

        var response = result.Value!;
        if (response.Success)
        {
            AnsiConsole.MarkupLine($"[green]OK[/] correlation=[yellow]{response.CorrelationId}[/]");
            if (response.Output is { } output)
            {
                AnsiConsole.WriteLine(output.ToString());
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Callee returned failure[/] code=[yellow]{response.ErrorCode}[/]");
            if (!string.IsNullOrEmpty(response.ErrorMessage))
            {
                AnsiConsole.WriteLine(response.ErrorMessage);
            }
        }
    }

    /// <summary>
    /// Demo handler for the workspace agent's search-tickets skill. Registered
    /// via the demo composition root: <c>services.AddKeyedScoped&lt;IA2ASkillHandler, WorkspaceSearchTicketsHandler&gt;("agent-workspace:search-tickets")</c>.
    /// </summary>
    public sealed class WorkspaceSearchTicketsHandler : IA2ASkillHandler
    {
        private readonly IAgentExecutionContext _executionContext;
        private readonly ILogger<WorkspaceSearchTicketsHandler> _logger;

        /// <summary>Creates a new handler.</summary>
        public WorkspaceSearchTicketsHandler(
            IAgentExecutionContext executionContext,
            ILogger<WorkspaceSearchTicketsHandler> logger)
        {
            _executionContext = executionContext;
            _logger = logger;
        }

        /// <inheritdoc />
        public Task<Result<A2AResponse>> HandleAsync(A2ARequest request, CancellationToken cancellationToken)
        {
            // The server's identity propagator stamped the caller's identity
            // onto our ambient context. Reading it back proves identity flowed
            // end-to-end.
            var caller = _executionContext.AgentIdentity;
            _logger.LogInformation(
                "Workspace handler invoked by caller {Caller} for task: {Task}",
                caller?.Id ?? "<unknown>",
                request.TaskDescription);

            var output = JsonSerializer.SerializeToElement(new
            {
                caller = caller?.Id ?? "<unknown>",
                tickets = new[]
                {
                    new { id = "INC-1042", severity = 2, summary = "DB connection pool exhaustion" },
                    new { id = "INC-1099", severity = 2, summary = "Auth latency spike" }
                }
            });

            return Task.FromResult(Result<A2AResponse>.Success(
                A2AResponse.Ok(request.Envelope.CorrelationId, output)));
        }
    }
}
