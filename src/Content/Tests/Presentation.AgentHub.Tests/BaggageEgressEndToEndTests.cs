using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// End-to-end proof that a caller-supplied "baggage" HTTP header does not reach
/// <see cref="Activity.Baggage"/> server-side on a real, running host — not just that a propagator
/// object reports the "right" <c>Fields</c> in isolation.
/// </summary>
/// <remarks>
/// CI's correctness-review and security-review gates both found the same gap by independent
/// analysis: ASP.NET Core's own hosting bootstrap (<c>GenericWebHostBuilder</c>, inside
/// <c>WebApplication.CreateBuilder</c>) captures whatever <c>DistributedContextPropagator.Current</c>
/// was into its own DI container as a singleton, BEFORE this harness's <c>AddOpenTelemetry</c> ever
/// runs — and its inbound request pipeline (<c>HostingApplicationDiagnostics</c>) resolves that DI
/// singleton via constructor injection, never re-reading the mutable static property per request. Every
/// prior test in this diff proved the propagator OBJECT behaves correctly when driven directly; none of
/// them proved a REAL inbound request through a REAL host actually uses that object. This test drives a
/// real request through <see cref="TestWebApplicationFactory"/> — the same composition root AgentHub
/// runs in production — and captures <see cref="Activity.Baggage"/> from server-side middleware
/// installed via <see cref="IStartupFilter"/>, the sanctioned way to add middleware to an already-built
/// minimal-API pipeline without touching <c>Program.cs</c>. The capture runs before any framework
/// middleware (auth, routing) so an authentication failure on the target endpoint does not affect what
/// this test observes.
/// </remarks>
public sealed class BaggageEgressEndToEndTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public BaggageEgressEndToEndTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task InboundRequestWithABaggageHeader_NeverReachesActivityBaggage()
    {
        KeyValuePair<string, string?>[]? capturedBaggage = null;

        using var client = _factory
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(
                    new BaggageCapturingStartupFilter(captured => capturedBaggage = captured))))
            .CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/health/ai");
        request.Headers.Add(
            "baggage",
            "agent.user_id=attacker-chosen,agent.conversation_id=victim-conversation-id");

        await client.SendAsync(request);

        capturedBaggage.Should().NotBeNull("the capturing middleware must have run for this request");
        capturedBaggage.Should().BeEmpty(
            "this host's baggage-egress policy defaults to suppressed (Observability:PropagateBaggage "
            + "is false), so a caller-supplied baggage header must never reach Activity.Baggage — the "
            + "exact gap CI's correctness-review and security-review gates found the propagator-object "
            + "tests alone could not catch");
    }

    /// <summary>
    /// Captures <see cref="Activity.Baggage"/> as the very first thing any request observes, before
    /// routing or authentication — <see cref="Activity.Current"/> is already fully populated by ASP.NET
    /// Core's hosting diagnostics by the time ANY <see cref="IApplicationBuilder"/> middleware runs, so
    /// this reflects exactly what the real inbound propagator extracted, independent of how the target
    /// endpoint itself responds.
    /// </summary>
    private sealed class BaggageCapturingStartupFilter(Action<KeyValuePair<string, string?>[]> onCaptured)
        : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    onCaptured((Activity.Current?.Baggage ?? []).ToArray());
                    await nextMiddleware();
                });
                next(app);
            };
    }
}
