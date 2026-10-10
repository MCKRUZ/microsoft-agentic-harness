using System.Runtime.CompilerServices;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Governance;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Infrastructure.Observability.Agent365;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.Observability.Tests.Agent365;

/// <summary>
/// A streamed Magentic participant run must carry the <em>participant's</em> Agent 365 attribution on
/// every step it takes, not only the first (#803) — measured against the real vendor SDK.
/// </summary>
/// <remarks>
/// <para>
/// Ambient state set inside an async iterator does not survive a <c>yield</c>: the consumer resumes the
/// iterator under its own context. The participant's context publishes its attribution once, when it is
/// armed inside the first step, so with no enclosing baggage the later steps — where most tool calls
/// happen — exported spans with no agent id, which Agent 365 discards without reporting an error.
/// </para>
/// <para>
/// Two baggage regimes are covered because OpenTelemetry keeps baggage behind a mutable holder that is
/// shared by every flow forked after it exists: <em>with</em> an enclosing holder (the supervisor's own
/// attribution was published first) a publish mutates that shared holder in place; <em>without</em> one it
/// lives only in the flow that published. The wrapper has to be right in both.
/// </para>
/// </remarks>
public class StreamedParticipantAttributionTests
{
    private const string SupervisorAppId = "11111111-1111-1111-1111-111111111111";
    private const string ParticipantAppId = "33333333-3333-3333-3333-333333333333";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";
    private const string Participant = "participant";

    private static Agent365TelemetryAttribution Attribution()
    {
        var appConfig = new AppConfig();
        var config = appConfig.Observability.Exporters.Agent365;
        config.Enabled = true;
        config.AgentAppId = SupervisorAppId;
        config.TenantId = TenantId;
        config.Agents[Participant] = new Agent365AgentIdentityConfig { AppId = ParticipantAppId };

        return new Agent365TelemetryAttribution(
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig),
            NullLogger<Agent365TelemetryAttribution>.Instance);
    }

    private static string Attributed()
    {
        var values = OpenTelemetry.Baggage.GetBaggage().Values;
        return values.Contains(ParticipantAppId) ? "participant"
            : values.Contains(SupervisorAppId) ? "supervisor"
            : "none";
    }

    /// <summary>
    /// Runs one three-step streamed participant under a real turn, returning what attribution was in
    /// force inside each step and what the consumer saw in between.
    /// </summary>
    private static async Task<(List<string> Inside, List<string> Consumer, string After)> RunAsync(
        bool supervisorPublishesFirst)
    {
        // Own flow, so baggage this test touches cannot reach another test through a shared holder.
        return await Task.Run(async () =>
        {
            var attribution = Attribution();
            var services = new ServiceCollection();
            services.AddSingleton<IAgentTelemetryAttribution>(attribution);
            services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
            services.AddScoped(_ => Mock.Of<IToolCallAdmissionPipeline>());
            await using var provider = services.BuildServiceProvider();

            await Task.Yield();

            // The supervisor's own context. With the real attribution it publishes first, which creates the
            // baggage holder every later flow shares; with the default no-op one it publishes nothing.
            using var parent = supervisorPublishesFirst
                ? new AgentExecutionContext(attribution)
                : new AgentExecutionContext();
            parent.Initialize("supervisor", "conv-1", 1, "conv-1");

            await using var turn = new ParticipantGovernance(
                provider.GetRequiredService<IServiceScopeFactory>(), parent,
                Mock.Of<IGovernanceTraceRecorder>()).ForTurn("conv-1");

            var inside = new List<string>();
            var probe = new SteppingAgent(inside);
            var consumer = new List<string>();
            await foreach (var _ in turn.Wrap(probe, Participant).RunStreamingAsync("go"))
                consumer.Add(Attributed());

            return (inside, consumer, Attributed());
        });
    }

    [Fact]
    public async Task EveryStepOfAStreamedParticipantCarriesTheParticipantsAttribution_WithNoEnclosingBaggage()
    {
        var (inside, _, _) = await RunAsync(supervisorPublishesFirst: false);

        inside.Should().Equal(
            ["participant", "participant", "participant"],
            "step 1 alone used to carry it; the later steps are where the tool calls happen");
    }

    [Fact]
    public async Task EveryStepOfAStreamedParticipantCarriesTheParticipantsAttribution_WithTheSupervisorsBaggageShared()
    {
        var (inside, _, _) = await RunAsync(supervisorPublishesFirst: true);

        inside.Should().Equal(["participant", "participant", "participant"]);
    }

    [Fact]
    public async Task TheSupervisorKeepsItsOwnAttribution_BetweenStepsAndAfterTheStream_WithTheBaggageShared()
    {
        // The shared holder is the production-likely regime: the supervisor's own turn publishes first, so
        // every flow forked after it shares one mutable holder, and a publish that is not released puts the
        // participant's identity on the supervisor's spans for the rest of the request.
        var (_, consumer, after) = await RunAsync(supervisorPublishesFirst: true);

        consumer.Should().OnlyContain(who => who == "supervisor");
        after.Should().Be("supervisor");
    }

    [Fact]
    public async Task TheConsumerBetweenSteps_DoesNotCarryTheParticipantsAttribution_WithNoEnclosingBaggage()
    {
        var (_, consumer, _) = await RunAsync(supervisorPublishesFirst: false);

        consumer.Should().OnlyContain(
            who => who != "participant",
            "the participant's attribution is scoped to its own steps and must not colour whatever consumes the stream");
    }

    /// <summary>A participant that reads the attribution in force at each of its steps.</summary>
    private sealed class SteppingAgent(List<string> inside) : AIAgent
    {
        protected override string IdCore => "stepping-id";
        public override string? Name => "stepping";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < 3; i++)
            {
                await Task.Yield();
                inside.Add(Attributed());
                yield return new AgentResponseUpdate(ChatRole.Assistant, $"update-{i}");
            }
        }
    }
}
