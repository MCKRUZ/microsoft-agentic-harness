using System.Text.Json;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Tests.Owasp.Invokers;

/// <summary>
/// Stub invoker for the ASI07 Insecure Inter-Agent Communication fixture.
/// Simulates the A2A channel validator rejecting both the non-HTTPS transport scheme
/// and the untrusted JWT issuer, with no outbound agent call made.
/// </summary>
public sealed class OwaspAsi07StubInvoker : IAgentInvoker
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
            httpRejectionCode = "a2a.scheme_not_allowed",
            jwtRejectionCode = "a2a.issuer_invalid",
            outboundCallCount = 0
        };

        return Task.FromResult(new AgentInvocationResult
        {
            Success = true,
            Output = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        });
    }
}
