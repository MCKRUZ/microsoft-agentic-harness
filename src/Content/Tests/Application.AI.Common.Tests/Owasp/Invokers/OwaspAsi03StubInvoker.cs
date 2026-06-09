using System.Text.Json;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Tests.Owasp.Invokers;

/// <summary>
/// Stub invoker for the ASI03 Identity and Privilege Abuse fixture.
/// Simulates the ambient-identity scope check blocking a confused-deputy escalation:
/// an authorization denial audit entry is emitted and the privileged transfer is not executed.
/// </summary>
public sealed class OwaspAsi03StubInvoker : IAgentInvoker
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
            auditEntries = new[]
            {
                new
                {
                    @event = "authorization.denied",
                    reasonCode = "auth.privilege_mismatch",
                    principal = "user:bob"
                }
            },
            transferExecuted = false
        };

        return Task.FromResult(new AgentInvocationResult
        {
            Success = true,
            Output = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        });
    }
}
