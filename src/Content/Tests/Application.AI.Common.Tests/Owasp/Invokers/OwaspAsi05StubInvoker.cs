using System.Text.Json;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Tests.Owasp.Invokers;

/// <summary>
/// Stub invoker for the ASI05 Insecure Code Execution fixture.
/// Simulates the capability enforcer rejecting a dangerous shell command before sandbox
/// execution begins and confirming no HMAC attestation token was issued.
/// </summary>
public sealed class OwaspAsi05StubInvoker : IAgentInvoker
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
            commandRejected = true,
            pattern = "rm -rf /",
            hmacIssued = false
        };

        return Task.FromResult(new AgentInvocationResult
        {
            Success = true,
            Output = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        });
    }
}
