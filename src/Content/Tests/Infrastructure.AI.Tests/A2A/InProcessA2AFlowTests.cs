using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Application.AI.Common.Interfaces.Agent;
using Domain.AI.A2A;
using Domain.AI.Identity;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.A2A;

/// <summary>
/// PR-7 acceptance tests for the in-process A2A flow. Covers tests 1, 4, 5
/// of the PR plan: in-process call success, identity propagation end-to-end,
/// and OTel span linking.
/// </summary>
public sealed class InProcessA2AFlowTests
{
    [Fact]
    public async Task InProcess_call_returns_response_with_correlation_id()
    {
        using var spans = new A2ATestHelpers.CapturedSpans();
        var envelope = A2ATestHelpers.MakeEnvelope();

        var (server, ctx) = A2ATestHelpers.BuildInProcessServer(
            calleeAgentId: "agent-b",
            handler: req => Task.FromResult(Result<A2AResponse>.Success(
                A2AResponse.Ok(
                    req.Envelope.CorrelationId,
                    JsonSerializer.SerializeToElement(new { ok = true })))));

        var client = A2ATestHelpers.BuildInProcessClient(
            server,
            A2ATestHelpers.MakeExecutionContext("agent-a"));

        var result = await client.CallAsync(A2ATestHelpers.MakeRequest(envelope), default);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Success.Should().BeTrue();
        result.Value.CorrelationId.Should().Be(envelope.CorrelationId);
    }

    [Fact]
    public async Task Identity_propagates_end_to_end()
    {
        IAgentExecutionContext? serverCtxSeen = null;
        var (server, _) = A2ATestHelpers.BuildInProcessServer(
            calleeAgentId: "agent-b",
            handler: req =>
            {
                // The handler runs inside the SAME execution context the
                // server's identity propagator wrote to. We assert by
                // capturing the context the test helper closed over.
                return Task.FromResult(Result<A2AResponse>.Success(
                    A2AResponse.Ok(req.Envelope.CorrelationId, JsonSerializer.SerializeToElement(new { })) ));
            });

        // The test helper exposes the server's execution context indirectly:
        // BuildInProcessServer constructs the context internally and the
        // server's propagator sets identity on it. We retrieve the context
        // by reflecting against the server's auth provider.
        var authField = typeof(Infrastructure.AI.A2A.HarnessA2AServer)
            .GetField("_identityPropagator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var propagator = authField!.GetValue(server);
        var ctxField = propagator!.GetType()
            .GetField("_executionContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        serverCtxSeen = (IAgentExecutionContext)ctxField!.GetValue(propagator)!;

        var client = A2ATestHelpers.BuildInProcessClient(
            server,
            A2ATestHelpers.MakeExecutionContext("agent-a"));

        var envelope = A2ATestHelpers.MakeEnvelope(callerAgentId: "agent-a", calleeAgentId: "agent-b");
        var result = await client.CallAsync(A2ATestHelpers.MakeRequest(envelope), default);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Success.Should().BeTrue();
        serverCtxSeen!.AgentIdentity.Should().NotBeNull();
        serverCtxSeen.AgentIdentity!.Id.Should().Be("agent-a");
    }

    [Fact]
    public async Task OTel_spans_share_correlation_id_and_link_caller_to_callee()
    {
        using var spans = new A2ATestHelpers.CapturedSpans();
        var envelope = A2ATestHelpers.MakeEnvelope(
            callerAgentId: "agent-a",
            calleeAgentId: "agent-b",
            calleeSkill: "search");

        var (server, _) = A2ATestHelpers.BuildInProcessServer(
            calleeAgentId: "agent-b",
            calleeSkill: "search",
            handler: req => Task.FromResult(Result<A2AResponse>.Success(
                A2AResponse.Ok(req.Envelope.CorrelationId, JsonSerializer.SerializeToElement(new { })))));

        var client = A2ATestHelpers.BuildInProcessClient(
            server,
            A2ATestHelpers.MakeExecutionContext("agent-a"));

        await client.CallAsync(A2ATestHelpers.MakeRequest(envelope), default);

        var clientSpan = spans.Activities
            .FirstOrDefault(a => a.DisplayName.StartsWith(A2AConventions.SpanNameClientPrefix));
        var serverSpan = spans.Activities
            .FirstOrDefault(a => a.DisplayName.StartsWith(A2AConventions.SpanNameServerPrefix));

        clientSpan.Should().NotBeNull();
        serverSpan.Should().NotBeNull();

        clientSpan!.Kind.Should().Be(ActivityKind.Client);
        serverSpan!.Kind.Should().Be(ActivityKind.Server);

        clientSpan.GetTagItem(A2AConventions.CalleeId).Should().Be("agent-b");
        serverSpan.GetTagItem(A2AConventions.CallerId).Should().Be("agent-a");

        clientSpan.GetTagItem(A2AConventions.CorrelationId)
            .Should().Be(serverSpan.GetTagItem(A2AConventions.CorrelationId));

        clientSpan.GetTagItem(A2AConventions.Transport).Should().Be(A2AConventions.TransportInProcess);
        clientSpan.GetTagItem(A2AConventions.AuthScheme).Should().Be(A2AConventions.AuthSchemeInProcess);
    }

    [Fact]
    public async Task Missing_skill_returns_skill_not_found()
    {
        var (server, _) = A2ATestHelpers.BuildInProcessServer(
            calleeAgentId: "agent-b",
            handler: req => Task.FromResult(Result<A2AResponse>.Success(
                A2AResponse.Ok(req.Envelope.CorrelationId, JsonSerializer.SerializeToElement(new { })))));

        var client = A2ATestHelpers.BuildInProcessClient(
            server,
            A2ATestHelpers.MakeExecutionContext("agent-a"));

        var envelope = A2ATestHelpers.MakeEnvelope(calleeAgentId: "agent-c"); // no handler
        var result = await client.CallAsync(A2ATestHelpers.MakeRequest(envelope), default);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Success.Should().BeFalse();
        result.Value.ErrorCode.Should().Be("a2a.skill_not_found");
    }
}
