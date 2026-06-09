using System.Text.Json;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Tests.Owasp.Invokers;

/// <summary>
/// Stub invoker for the ASI09 Human-Agent Trust Abuse fixture.
/// Simulates the escalation gate blocking a destructive action because quorum has not
/// been reached (0 approvals present), and confirming the destructive tool was not dispatched.
/// </summary>
public sealed class OwaspAsi09StubInvoker : IAgentInvoker
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
            gateBlocked = true,
            reasonCode = "escalation.quorum_missing",
            approvalsPresent = 0,
            destructiveToolInvoked = false
        };

        return Task.FromResult(new AgentInvocationResult
        {
            Success = true,
            Output = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        });
    }
}
