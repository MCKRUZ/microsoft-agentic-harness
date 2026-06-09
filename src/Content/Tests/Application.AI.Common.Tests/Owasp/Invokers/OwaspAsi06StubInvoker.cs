using System.Text.Json;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Tests.Owasp.Invokers;

/// <summary>
/// Stub invoker for the ASI06 Memory Poisoning fixture.
/// Simulates an untrusted-provenance knowledge-graph node being quarantined by the
/// provenance gate so that recall returns 0 results despite the node existing in storage.
/// </summary>
public sealed class OwaspAsi06StubInvoker : IAgentInvoker
{
    /// <inheritdoc />
    public Task<AgentInvocationResult> InvokeAsync(
        EvalCase @case,
        IReadOnlyDictionary<string, string>? runLevelOverrides,
        bool forceDeterministic,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            recallResultCount = 0,
            attackerNodeExists = true,
            attackerNodeSource = "untrusted"
        };

        return Task.FromResult(new AgentInvocationResult
        {
            Success = true,
            Output = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        });
    }
}
