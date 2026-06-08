using Application.AI.Common.Services.Agent;
using Domain.AI.A2A;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.A2A;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.A2A;

/// <summary>
/// PR-7 acceptance tests for the cross-process A2A auth path. Covers tests 2
/// and 3 of the PR plan: a valid JWT flows through and yields the
/// authoritative caller id; an invalid JWT surfaces as
/// <c>a2a.auth_rejected</c>.
/// </summary>
/// <remarks>
/// We exercise the cross-process auth provider directly rather than spinning
/// up a Kestrel mTLS listener — the listener side belongs to the consumer
/// (Kestrel + IIS configuration), and the cross-process branch of
/// <see cref="HarnessA2AServer"/> takes the same code path regardless of how
/// the transport handed it the envelope + headers.
/// </remarks>
public sealed class CrossProcessA2AFlowTests
{
    [Fact]
    public async Task Valid_JWT_yields_authoritative_caller_id()
    {
        var acquirer = new StubTokenAcquirer("valid-token-for-agent-a");
        var validator = new StubTokenValidator(
            jwt => jwt == "valid-token-for-agent-a"
                ? Result<string>.Success("agent-a")
                : Result<string>.Fail("a2a.auth_rejected"));

        var provider = new CrossProcessA2AAuthenticationProvider(
            acquirer,
            validator,
            NullLogger<CrossProcessA2AAuthenticationProvider>.Instance);

        var envelope = A2ATestHelpers.MakeEnvelope(callerAgentId: "agent-a");
        var outboundCreds = await provider.StampOutboundCredentialsAsync(envelope, default);
        outboundCreds.IsSuccess.Should().BeTrue();
        outboundCreds.Value!.Should().ContainKey("Authorization");
        outboundCreds.Value["Authorization"].Should().Be("Bearer valid-token-for-agent-a");

        var inbound = await provider.ValidateInboundAsync(envelope, outboundCreds.Value, default);
        inbound.IsSuccess.Should().BeTrue();
        inbound.Value.Should().Be("agent-a");
    }

    [Fact]
    public async Task Invalid_JWT_returns_auth_rejected()
    {
        var validator = new StubTokenValidator(
            _ => Result<string>.Fail("a2a.auth_rejected"));

        var provider = new CrossProcessA2AAuthenticationProvider(
            new StubTokenAcquirer("bad-jwt"),
            validator,
            NullLogger<CrossProcessA2AAuthenticationProvider>.Instance);

        var envelope = A2ATestHelpers.MakeEnvelope(callerAgentId: "agent-a");
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer bad-jwt" };

        var result = await provider.ValidateInboundAsync(envelope, headers, default);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("a2a.auth_rejected");
    }

    [Fact]
    public async Task JWT_sub_mismatch_with_envelope_caller_rejects()
    {
        var validator = new StubTokenValidator(
            // JWT sub says "agent-b" but envelope says "agent-a" — must reject.
            _ => Result<string>.Success("agent-b"));

        var provider = new CrossProcessA2AAuthenticationProvider(
            new StubTokenAcquirer("token-for-agent-b"),
            validator,
            NullLogger<CrossProcessA2AAuthenticationProvider>.Instance);

        var envelope = A2ATestHelpers.MakeEnvelope(callerAgentId: "agent-a");
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token-for-agent-b" };

        var result = await provider.ValidateInboundAsync(envelope, headers, default);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("a2a.auth_rejected");
    }

    [Fact]
    public async Task Missing_authorization_header_rejects()
    {
        var provider = new CrossProcessA2AAuthenticationProvider(
            new StubTokenAcquirer("ignored"),
            new StubTokenValidator(_ => Result<string>.Success("agent-a")),
            NullLogger<CrossProcessA2AAuthenticationProvider>.Instance);

        var envelope = A2ATestHelpers.MakeEnvelope(callerAgentId: "agent-a");
        var emptyHeaders = new Dictionary<string, string>();

        var result = await provider.ValidateInboundAsync(envelope, emptyHeaders, default);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain("a2a.auth_rejected");
    }

    private sealed class StubTokenAcquirer : IA2ATokenAcquirer
    {
        private readonly string _token;
        public StubTokenAcquirer(string token) => _token = token;
        public Task<Result<string>> AcquireAsync(A2AEnvelope envelope, CancellationToken ct)
            => Task.FromResult(Result<string>.Success(_token));
    }

    private sealed class StubTokenValidator : IA2ATokenValidator
    {
        private readonly Func<string, Result<string>> _impl;
        public StubTokenValidator(Func<string, Result<string>> impl) => _impl = impl;
        public Task<Result<string>> ValidateAsync(string jwt, CancellationToken ct)
            => Task.FromResult(_impl(jwt));
    }
}
