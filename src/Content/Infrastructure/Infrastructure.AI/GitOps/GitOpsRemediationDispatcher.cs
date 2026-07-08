using Application.AI.Common.CQRS.Changes.SubmitChangeProposal;
using Application.AI.Common.Interfaces.GitOps;
using Domain.AI.Changes;
using Domain.AI.GitOps;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.GitOps;

/// <summary>
/// The single sanctioned write path for the GitOps skill pack. Translates a
/// <see cref="RemediationPlan"/> into a <see cref="SubmitChangeProposalCommand"/>
/// and submits it via <see cref="IMediator"/>, surfacing the resulting
/// <see cref="ChangeProposal"/> back to the caller for audit logging.
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher is the only place in <c>Infrastructure.AI.GitOps</c> permitted
/// to issue mutating intent. Tools, controllers, and skills NEVER write
/// directly to a Git repo or to the cluster — they hand off to this dispatcher,
/// which routes through the PR-2 gate pipeline.
/// </para>
/// <para>
/// The skill key on the submitted command (<c>gitops:{controller}</c>) lets the
/// graded-autonomy resolver apply per-controller skill overrides. The
/// <c>IsStateChange</c> flag is unconditionally true: every remediation
/// edits files in a Git repo, so even an Autonomous tier should round-trip
/// through approval unless the operator has explicitly opted-in via
/// <c>GradedAutonomyConfig.StateChangerOptIns</c>.
/// </para>
/// </remarks>
public sealed class GitOpsRemediationDispatcher : IGitOpsRemediationDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GitOpsRemediationDispatcher> _logger;

    /// <summary>Initializes a new <see cref="GitOpsRemediationDispatcher"/>.</summary>
    /// <param name="scopeFactory">Scope factory used to resolve <see cref="IMediator"/> per dispatch.
    /// The dispatcher is a SINGLETON, but a mediator dispatch constructs pipeline behaviors that
    /// ctor-inject the SCOPED <c>IAgentExecutionContext</c>, so each dispatch runs inside a fresh
    /// scope rather than against a root-bound mediator.</param>
    /// <param name="logger">Logger for dispatch diagnostics.</param>
    public GitOpsRemediationDispatcher(IServiceScopeFactory scopeFactory, ILogger<GitOpsRemediationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ChangeProposal>> DispatchAsync(RemediationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Edits.Count == 0)
        {
            return Result<ChangeProposal>.Fail("gitops.remediation.empty_plan");
        }

        var skillKey = $"gitops:{plan.ControllerKind.ToString().ToLowerInvariant()}";
        var summary = string.IsNullOrEmpty(plan.Summary)
            ? $"GitOps remediation for {plan.SourceDrift.DriftedResources.Count} drifted resource(s)."
            : plan.Summary;

        var command = new SubmitChangeProposalCommand
        {
            Target = plan.Target,
            Diff = plan.Edits,
            Summary = summary,
            BlastRadius = plan.ProposedBlastRadius,
            IsStateChange = true,
            SkillKey = skillKey
        };

        try
        {
            // Dispatch inside a fresh scope: the MediatR pipeline resolves scoped services
            // (IAgentExecutionContext et al.), which a singleton must never pull from the root.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var result = await mediator.Send(command, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "GitOps remediation submission failed for plan from {Controller}: {Errors}",
                    plan.ControllerKind,
                    string.Join("; ", result.Errors));
                return result;
            }

            _logger.LogInformation(
                "GitOps remediation submitted: controller={Controller} proposalId={ProposalId} resources={ResourceCount}",
                plan.ControllerKind,
                result.Value!.Id,
                plan.SourceDrift.DriftedResources.Count);

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "GitOps remediation submission threw unexpectedly.");
            return Result<ChangeProposal>.Fail("gitops.remediation.dispatch_failed");
        }
    }
}
