using Application.AI.Common.Interfaces.Egress;
using Domain.AI.Egress;
using Domain.AI.Identity;
using FluentAssertions;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Sandbox;
using Infrastructure.AI.Tests.Egress.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Sandbox;

/// <summary>
/// PR-3c tests 7 + 8 — sandbox preflight composed with the real egress policy:
/// (7) an allowlisted target is approved and logged to the audit as an Allow;
/// (8) a non-allowlisted target is denied and surfaces as a deny in the
/// audit + a non-allowed decision in the returned list (which the executor
/// translates into <c>EgressBlockedException</c>-style fail-fast on the
/// sandbox boundary). The preflight does NOT throw — the executor decides
/// whether to abort, which keeps the audit trail uniform.
/// </summary>
public sealed class SandboxEgressPreflightTests
{
    private static readonly AgentIdentity Identity = new()
    {
        Id = "sandbox-skill",
        Kind = AgentIdentityKind.Development,
        TenantId = "tenant-1"
    };

    private static DefaultEgressPolicy BuildPolicy(params EgressAllowlistEntry[] entries) =>
        new(entries, NullLogger<DefaultEgressPolicy>.Instance, TimeProvider.System);

    private sealed class StubResolver : IEgressPolicyResolver
    {
        private readonly Domain.AI.Egress.IEgressPolicy _policy;
        public StubResolver(Domain.AI.Egress.IEgressPolicy policy) { _policy = policy; }
        public Domain.AI.Egress.IEgressPolicy ResolveFor(AgentIdentity identity) => _policy;
    }

    [Fact]
    public async Task EvaluateAsync_AllowlistedTarget_AllowsAndAuditsAllow()
    {
        var policy = BuildPolicy(new EgressAllowlistEntry
        {
            Host = "api.github.com",
            Schemes = ["https"],
            Ports = [443]
        });
        var audit = new InMemoryEgressAuditWriter();
        var preflight = new SandboxEgressPreflight(
            new ServiceCollection().BuildServiceProvider(),
            new FakeAmbientRequestScope(Identity),
            new StubResolver(policy),
            audit,
            NullLogger<SandboxEgressPreflight>.Instance,
            TimeProvider.System);

        var decisions = await preflight.EvaluateAsync(
            [new Uri("https://api.github.com/users/octocat")],
            CancellationToken.None);

        decisions.Should().HaveCount(1);
        decisions[0].Allowed.Should().BeTrue();
        decisions[0].MatchedAllowlistEntry.Should().Be("api.github.com");

        audit.Entries.Should().HaveCount(1);
        audit.Entries.TryDequeue(out var entry).Should().BeTrue();
        entry.Decision.Allowed.Should().BeTrue();
        entry.Identity.Id.Should().Be(Identity.Id);
    }

    [Fact]
    public async Task EvaluateAsync_NonAllowlistedTarget_DeniesAndAuditsDeny()
    {
        var policy = BuildPolicy(new EgressAllowlistEntry
        {
            Host = "api.github.com",
            Schemes = ["https"],
            Ports = [443]
        });
        var audit = new InMemoryEgressAuditWriter();
        var preflight = new SandboxEgressPreflight(
            new ServiceCollection().BuildServiceProvider(),
            new FakeAmbientRequestScope(Identity),
            new StubResolver(policy),
            audit,
            NullLogger<SandboxEgressPreflight>.Instance,
            TimeProvider.System);

        var decisions = await preflight.EvaluateAsync(
            [new Uri("https://evil.example.com/exfil")],
            CancellationToken.None);

        decisions.Should().HaveCount(1);
        decisions[0].Allowed.Should().BeFalse();

        audit.Entries.Should().HaveCount(1);
        audit.Entries.TryDequeue(out var entry).Should().BeTrue();
        entry.Decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_NoAgentIdentity_DeniesAll()
    {
        // Sandbox preflight without an attributable identity short-circuits to
        // deny — the policy refuses to make a verdict without identity so the
        // audit trail and per-skill allowlists remain meaningful.
        var policy = BuildPolicy(new EgressAllowlistEntry
        {
            Host = "api.github.com",
            Schemes = ["https"],
            Ports = [443]
        });
        var audit = new InMemoryEgressAuditWriter();
        var preflight = new SandboxEgressPreflight(
            new ServiceCollection().BuildServiceProvider(),
            new FakeAmbientRequestScope(identity: null),
            new StubResolver(policy),
            audit,
            NullLogger<SandboxEgressPreflight>.Instance,
            TimeProvider.System);

        var decisions = await preflight.EvaluateAsync(
            [new Uri("https://api.github.com/")],
            CancellationToken.None);

        decisions.Should().HaveCount(1);
        decisions[0].Allowed.Should().BeFalse();
        audit.Entries.Should().HaveCount(1);
    }

    [Fact]
    public void ComputeDigest_EmptyList_ReturnsEmptyString()
    {
        var preflight = new SandboxEgressPreflight(
            new ServiceCollection().BuildServiceProvider(),
            new FakeAmbientRequestScope(Identity),
            new StubResolver(BuildPolicy()),
            new InMemoryEgressAuditWriter(),
            NullLogger<SandboxEgressPreflight>.Instance,
            TimeProvider.System);

        preflight.ComputeDigest([]).Should().BeEmpty();
    }

    [Fact]
    public void ComputeDigest_NonEmptyDecisions_IsDeterministic()
    {
        var preflight = new SandboxEgressPreflight(
            new ServiceCollection().BuildServiceProvider(),
            new FakeAmbientRequestScope(Identity),
            new StubResolver(BuildPolicy()),
            new InMemoryEgressAuditWriter(),
            NullLogger<SandboxEgressPreflight>.Instance,
            TimeProvider.System);

        var decisions = new EgressDecision[]
        {
            new()
            {
                Allowed = true,
                Reason = "ok",
                MatchedAllowlistEntry = "api.github.com",
                Target = new Uri("https://api.github.com/"),
                DecidedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            }
        };

        var d1 = preflight.ComputeDigest(decisions);
        var d2 = preflight.ComputeDigest(decisions);

        d1.Should().Be(d2);
        d1.Length.Should().Be(64); // SHA-256 hex
    }
}
