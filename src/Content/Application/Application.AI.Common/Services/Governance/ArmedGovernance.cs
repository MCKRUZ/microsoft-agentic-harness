using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// A child governance scope that has been set up by <see cref="GovernanceArmer.Arm"/>: its execution
/// context is initialised and, when the policy asked for it, its admission pipeline is resolved and
/// reset. It owns nothing — the caller still owns the DI scope the services came from.
/// </summary>
public sealed class ArmedGovernance
{
    private readonly IToolCallAdmissionPipeline? _pipeline;

    internal ArmedGovernance(
        IAgentExecutionContext context, IToolCallAdmissionPipeline? pipeline, string agentId)
    {
        Context = context;
        _pipeline = pipeline;
        AgentId = agentId;
    }

    /// <summary>The child scope's execution context, already initialised.</summary>
    public IAgentExecutionContext Context { get; }

    /// <summary>The agent id the child was initialised under.</summary>
    public string AgentId { get; }

    /// <summary>
    /// The child scope's admission pipeline, reset and ready. Throws when the policy did not resolve
    /// one — asking for a pipeline that was never armed is a programming error, not an absent value.
    /// </summary>
    public IToolCallAdmissionPipeline Pipeline => _pipeline
        ?? throw new InvalidOperationException(
            "This governance scope was armed without a pipeline (GovernanceArmingPolicy.ResolvePipeline is false).");

    /// <summary>
    /// Publishes this scope's pipeline as the ambient admission chain for the current async flow, so
    /// every governed tool call beneath it is authorized against THIS scope's agent rather than whatever
    /// an enclosing turn had armed. Dispose the returned handle to restore the previous chain.
    /// </summary>
    public IDisposable Activate() => ToolAdmissionAccessor.Begin(Pipeline);
}
