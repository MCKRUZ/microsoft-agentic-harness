using Application.AI.Common.Services.Agent;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.Observability.Agent365;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.Observability.Tests.Agent365;

/// <summary>
/// Proves that <em>every</em> turn a single execution context serves carries Agent 365 attribution —
/// not only the first — against the real vendor SDK rather than a stand-in.
/// </summary>
/// <remarks>
/// <para>
/// One DI scope can serve several turns: a conversation dispatches each turn as a sibling send inside
/// the scope it owns, and <c>AgentExecutionContext.Initialize</c> is called once per turn (that is what
/// updates the turn number). Attribution, however, is <em>ambient to the async flow that publishes
/// it</em>. Publishing only on the first turn therefore leaves later turns relying on the first turn's
/// values still being reachable from a sibling flow — which they sometimes are and sometimes are not,
/// depending on where <c>await</c>s happen to fall between the dispatch and the publish.
/// </para>
/// <para>
/// That is not a property worth depending on: the consequence of getting it wrong is spans a
/// governance platform discards <strong>without reporting an error</strong>, so a conversation would
/// appear in the tenant's records with its first turn only and nothing to say the rest were dropped.
/// The context republishes per turn so each turn's own flow carries attribution unconditionally.
/// Found by the correctness gate on the first cut of #737, which published once per context.
/// </para>
/// <para>
/// These tests assert <em>inside</em> each simulated turn, because that is when the turn's spans would
/// be created and therefore the only moment the values have to be in effect.
/// </para>
/// </remarks>
public class MultiTurnAttributionTests
{
    private const string HostAppId = "11111111-1111-1111-1111-111111111111";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";

    /// <remarks>
    /// Deliberately does <strong>not</strong> clear baggage, unlike the sibling suites. OpenTelemetry
    /// keeps baggage in a mutable holder behind an async-local, so <em>touching</em> baggage in this
    /// flow creates a holder that every turn below then shares and mutates in place — which makes one
    /// turn's publish visible to its siblings and hides the very failure these tests exist to catch. A
    /// production turn is reached without anything having established a holder first.
    /// </remarks>
    private static AgentExecutionContext BuildContext()
    {
        var appConfig = new AppConfig();
        var config = appConfig.Observability.Exporters.Agent365;
        config.Enabled = true;
        config.AgentAppId = HostAppId;
        config.TenantId = TenantId;

        return new AgentExecutionContext(new Agent365TelemetryAttribution(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig),
            NullLogger<Agent365TelemetryAttribution>.Instance));
    }

    [Fact]
    public async Task EveryTurnInOneScope_CarriesAttributionWhileItRuns()
    {
        using var context = BuildContext();

        // Sibling turns, each dispatched from this flow the way a conversation dispatches them, each
        // with an await before the context is initialized so the publish lands on the turn's own flow
        // rather than leaking into this one. Turn 2 is the case that regressed.
        for (var turn = 1; turn <= 3; turn++)
        {
            await RunTurnAsync(context, turn);
        }
    }

    [Fact]
    public async Task ALaterTurn_IsAttributedEvenWhenTheFirstTurnsFlowHasEnded()
    {
        using var context = BuildContext();

        await RunTurnAsync(context, 1);

        // Nothing from turn 1 is relied upon here: this flow observes whatever survived, and the turn
        // then publishes for itself.
        await RunTurnAsync(context, 2);
    }

    [Fact]
    public async Task AttributionOutlivesTheTurnButNotTheScope()
    {
        // Pins the one behaviour this design trades away, so it is a recorded decision rather than an
        // untested assumption. Releasing on scope disposal instead of at the end of the turn means that
        // where an enclosing flow already owns the ambient baggage — it does here, because this test
        // touches baggage before the turn, and in production whenever an inbound request carries a
        // baggage header — the turn's attribution stays in effect for the remainder of the request.
        //
        // That is an accuracy cost, not a disclosure one: it is the same agent and tenant, so later
        // activity in the same request is attributed to the agent that ran in it. What must NOT happen is
        // attribution surviving the scope, which would attribute a later, unrelated request's work; the
        // final assertion is that one.
        OpenTelemetry.Baggage.ClearBaggage();

        var context = BuildContext();
        await RunTurnAsync(context, 1);

        OpenTelemetry.Baggage.GetBaggage().Values.Should().Contain(
            HostAppId, "the accepted trade-off: attribution outlasts the turn within the request");

        context.Dispose();

        OpenTelemetry.Baggage.GetBaggage().Should().BeEmpty(
            "it must not outlast the scope — a later request must not inherit this agent's identity");
    }

    private static async Task RunTurnAsync(AgentExecutionContext context, int turnNumber)
    {
        await Task.Yield();

        context.Initialize("planner", "conv-1", turnNumber);

        await Task.Yield();

        var baggage = OpenTelemetry.Baggage.GetBaggage();
        baggage.Values.Should().Contain(
            HostAppId,
            "turn {0}'s spans are created here, and Agent 365 discards a span with no agent id without "
            + "reporting an error",
            turnNumber);
        baggage.Values.Should().Contain(TenantId, "the tenant half is dropped just as silently");
    }
}
