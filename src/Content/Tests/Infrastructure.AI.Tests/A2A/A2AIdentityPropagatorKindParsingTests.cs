using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Services.Agent;
using Domain.AI.A2A;
using Domain.AI.Identity;
using FluentAssertions;
using Infrastructure.AI.A2A;
using Xunit;

namespace Infrastructure.AI.Tests.A2A;

/// <summary>
/// Covers how <see cref="A2AIdentityPropagator.EstablishInboundIdentity"/> parses the
/// <see cref="A2AEnvelope.CallerKind"/> it is handed (#300).
/// </summary>
/// <remarks>
/// <para>
/// This is the one conversion in the #300 sweep that a caller can reach from outside the process.
/// <c>CallerKind</c> is a wire field, and <see cref="AgentIdentityKind.Unspecified"/> is the value
/// every reader treats as "identity not established".
/// </para>
/// <para>
/// A bare <c>Enum.TryParse</c> accepts <c>"99"</c> and returns a kind that is not a defined member —
/// and, decisively, not <c>Unspecified</c> either, so it looks resolved to anything testing for
/// Unspecified. <c>EntraAgentIdentityValidator.CanInvoke</c> is written to deny on exactly that
/// condition.
/// </para>
/// <para>
/// <strong>That validator had no production caller when these tests were written (measured 8 Aug
/// 2026), and has one now.</strong> #311 wired it onto the tool-call admission chain as stage 1,
/// so from an enabled host every tool call reaches it. These tests pinned the contract the wire
/// field must honour before the control existed — an unrecognised kind resolves to Unspecified —
/// which is precisely why wiring it up did not land it on a parser that had already lost the
/// distinction.
/// </para>
/// </remarks>
public sealed class A2AIdentityPropagatorKindParsingTests
{
    private static (A2AIdentityPropagator Propagator, IAgentExecutionContext Context) Build()
    {
        var context = new AgentExecutionContext();
        return (new A2AIdentityPropagator(context), context);
    }

    private static A2AEnvelope EnvelopeWithKind(string callerKind) => new()
    {
        SchemaVersion = A2AEnvelope.CurrentSchemaVersion,
        CorrelationId = Guid.NewGuid().ToString("N"),
        CallerAgentId = "agent-a",
        CallerKind = callerKind,
        CalleeAgentId = "agent-b"
    };

    [Theory]
    [InlineData("99")]                          // outside the defined range
    [InlineData(" 99")]                         // and behind a stray space
    [InlineData("2")]                           // the numeric form of a real member
    [InlineData("Unspecified,Development")]     // comma-composite
    [InlineData("NotAKind")]
    [InlineData("")]
    public void EstablishInboundIdentity_NonNameCallerKind_LandsOnUnspecified(string callerKind)
    {
        var (sut, context) = Build();

        sut.EstablishInboundIdentity("authoritative-caller", EnvelopeWithKind(callerKind));

        // Unspecified is the value the RBAC deny gate keys on. Anything the harness cannot recognise
        // must arrive there rather than at an undefined member that merely looks resolved.
        context.AgentIdentity!.Kind.Should().Be(AgentIdentityKind.Unspecified);
    }

    [Fact]
    public void EstablishInboundIdentity_UndefinedNumericKind_IsNotMistakenForAResolvedIdentity()
    {
        // The discriminating assertion, stated as the deny gate states it. Proof the guard is
        // load-bearing rather than decorative: the framework call this replaces accepts the same
        // input, and the value it produces passes the `Kind != Unspecified` test.
        Enum.TryParse<AgentIdentityKind>("99", out var viaFramework).Should().BeTrue();
        viaFramework.Should().NotBe(AgentIdentityKind.Unspecified);
        Enum.IsDefined(viaFramework).Should().BeFalse();

        var (sut, context) = Build();

        sut.EstablishInboundIdentity("authoritative-caller", EnvelopeWithKind("99"));

        context.AgentIdentity!.Kind.Should().Be(AgentIdentityKind.Unspecified);
    }

    [Theory]
    [InlineData("Development", AgentIdentityKind.Development)]
    [InlineData("development", AgentIdentityKind.Development)]
    [InlineData("ManagedIdentity", AgentIdentityKind.ManagedIdentity)]
    public void EstablishInboundIdentity_NamedCallerKind_IsHonoured(string callerKind, AgentIdentityKind expected)
    {
        // The control: refusing non-names must not mean refusing the real thing. The writing side
        // (StampOutboundIdentity) emits Kind.ToString(), so these are the values actually on the wire.
        var (sut, context) = Build();

        sut.EstablishInboundIdentity("authoritative-caller", EnvelopeWithKind(callerKind));

        context.AgentIdentity!.Kind.Should().Be(expected);
    }

    [Fact]
    public void EstablishInboundIdentity_AlwaysUsesTheAuthoritativeCallerId_NotTheEnvelopes()
    {
        // Guards the reason this is defence-in-depth rather than a bypass: the id that authorization
        // keys on comes from the auth provider, never from the envelope.
        var (sut, context) = Build();
        var envelope = EnvelopeWithKind(AgentIdentityKind.Development.ToString());

        sut.EstablishInboundIdentity("authoritative-caller", envelope);

        context.AgentIdentity!.Id.Should().Be("authoritative-caller");
        context.AgentIdentity.Id.Should().NotBe(envelope.CallerAgentId);
    }
}
