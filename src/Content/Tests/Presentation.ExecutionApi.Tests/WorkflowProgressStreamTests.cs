using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Presentation.ExecutionApi.Tests.WorkflowRequests;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Watches a real workflow run over Server-Sent-Events, through the real host.
/// </summary>
/// <remarks>
/// <para>
/// The whole feature is a chain of things that each look fine alone: the planner announces progress,
/// a bridge attributes it to a run, a broker fans it out, and an endpoint writes it. Any one of those
/// links being absent produces the same symptom — a stream that opens, says nothing, and closes — so
/// only an end-to-end assertion distinguishes "working" from "silently disconnected". The ExecutionApi
/// host in particular received the no-op notifier by default until this wave replaced it.
/// </para>
/// <para>
/// Only the model call is substituted. Everything else is the host's own.
/// </para>
/// </remarks>
public sealed class WorkflowProgressStreamTests
{
    /// <summary>Stands in for the model call, holding the step open until the test releases it.</summary>
    private sealed class GatedStepExecutor(SemaphoreSlim release) : IPlanStepExecutor
    {
        public async Task<StepExecutionResult> ExecuteAsync(
            PlanStep step,
            IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
            CancellationToken ct)
        {
            await release.WaitAsync(ct);

            return new StepExecutionResult
            {
                Status = StepExecutionStatus.Completed,
                Output = "done",
                Duration = TimeSpan.FromMilliseconds(1)
            };
        }
    }

    /// <summary>
    /// How long a stream may stay open before the test gives up on it.
    /// </summary>
    /// <remarks>
    /// A deadlock guard, not a performance assertion. A passing test returns the moment the server
    /// closes the stream, so a generous budget costs nothing — while a tight one turns a loaded build
    /// agent into a failure, which is what a 20-second budget did here when the rest of the suite was
    /// competing for the machine.
    /// </remarks>
    private static readonly TimeSpan StreamBudget = TimeSpan.FromSeconds(90);

    private readonly SemaphoreSlim _release = new(0, 1);

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(HeaderIdentityAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>(
                        HeaderIdentityAuthenticationHandler.SchemeName, _ => { });

                services.AddKeyedScoped<IPlanStepExecutor>(
                    StepType.LlmCall, (_, _) => new GatedStepExecutor(_release));
            }));

    private static HttpRequestMessage Request(HttpMethod method, string url, object? body, string oid)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        request.Headers.Add(HeaderIdentityAuthenticationHandler.UserHeader, oid);
        request.Headers.Add(HeaderIdentityAuthenticationHandler.TenantHeader, "acme");
        return request;
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string oid)
    {
        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/api/workflows", Definition([LlmStep("only")]), oid));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "submission failed with body: {0}", body);
        return JsonDocument.Parse(body).RootElement.GetProperty("workflowId").GetGuid();
    }

    private static async Task<string> StartAsync(HttpClient client, Guid workflowId, string oid)
    {
        var started = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/workflows/{workflowId}/runs", body: null, oid));

        var body = await started.Content.ReadAsStringAsync();
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, "start failed with body: {0}", body);
        return JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()!;
    }

    /// <summary>
    /// Reads SSE frames, reporting both what arrived and whether the server actually closed the
    /// stream.
    /// </summary>
    /// <remarks>
    /// <c>Closed</c> is load-bearing and not a detail: "sent one frame and finished" and "sent one
    /// frame then held the connection open until the client gave up" produce identical frame lists.
    /// A test that only counted frames would pass against a stream that hangs, which is precisely the
    /// failure a client would experience as the feature being broken.
    /// </remarks>
    private static async Task<(List<JsonElement> Frames, bool Closed)> ReadFramesAsync(
        Stream stream, TimeSpan budget)
    {
        var frames = new List<JsonElement>();
        using var reader = new StreamReader(stream);
        using var cts = new CancellationTokenSource(budget);

        try
        {
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;

                frames.Add(JsonDocument.Parse(line[6..]).RootElement.Clone());
            }

            return (frames, true);
        }
        catch (OperationCanceledException)
        {
            // Budget spent with the stream still open.
            return (frames, false);
        }
    }

    [Fact]
    public async Task AWatcherSeesTheRunProgressAndFinish()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitAsync(client, "alice");
        var jobId = await StartAsync(client, workflowId, "alice");

        var response = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/workflows/{workflowId}/runs/{jobId}/stream", null, "alice"),
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var stream = await response.Content.ReadAsStreamAsync();
        var reading = ReadFramesAsync(stream, StreamBudget);

        // Released only once the stream is open, so the step's progress cannot have been published
        // before anyone was listening — which is what makes this an assertion about live delivery
        // rather than about the snapshot.
        await Task.Delay(500);
        _release.Release();

        var (frames, closed) = await reading;

        var types = frames.Select(f => f.GetProperty("type").GetString()).ToList();
        var seen = string.Join(", ", types);

        frames.Should().NotBeEmpty("the stream must carry the run, not merely open");
        closed.Should().BeTrue(
            "the stream must close when the run ends, not hold the client open. Frames seen: [{0}]", seen);
        types.Should().StartWith(["SNAPSHOT"], "every stream opens by saying where the run already is");
        types.Should().Contain("STEP", "the run's steps must actually reach the watcher");
        types.Should().Contain("FINISHED", "the stream must report the run reaching its end");
    }

    [Fact]
    public async Task AStreamForARunThatAlreadyFinished_ReportsItAndCloses()
    {
        // A watcher that arrives late must not hang waiting for events that will never come.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        _release.Release();

        var workflowId = await SubmitAsync(client, "alice");
        var jobId = await StartAsync(client, workflowId, "alice");

        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(250);
            var poll = await client.SendAsync(
                Request(HttpMethod.Get, $"/api/workflows/{workflowId}/runs/{jobId}", null, "alice"));

            if (poll.StatusCode != HttpStatusCode.OK)
                continue;

            var status = (await poll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString();
            if (status is not ("Queued" or "Running"))
                break;
        }

        var response = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/workflows/{workflowId}/runs/{jobId}/stream", null, "alice"),
            HttpCompletionOption.ResponseHeadersRead);

        var (frames, closed) = await ReadFramesAsync(
            await response.Content.ReadAsStreamAsync(), StreamBudget);

        // Closing is the assertion that matters. A stream that sends the snapshot and then waits for
        // events that can never come produces an identical frame list, and the client experiences it
        // as the feature hanging.
        closed.Should().BeTrue("a finished run's stream must end rather than hold the connection open");
        frames.Should().ContainSingle("a finished run has nothing left to report beyond where it ended");
        frames[0].GetProperty("type").GetString().Should().Be("SNAPSHOT");
        frames[0].GetProperty("isTerminal").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task StreamingSomeoneElsesRun_Is404()
    {
        // Same answer as reading it. A stream that distinguished "not yours" from "not there" would
        // let a caller discover work it was never given the identifier for.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitAsync(client, "alice");
        var jobId = await StartAsync(client, workflowId, "alice");

        var stolen = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/workflows/{workflowId}/runs/{jobId}/stream", null, "mallory"),
            HttpCompletionOption.ResponseHeadersRead);

        stolen.StatusCode.Should().Be(HttpStatusCode.NotFound);

        _release.Release();
    }

    [Fact]
    public async Task StreamingARunUnderTheWrongWorkflow_Is404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitAsync(client, "alice");
        var otherWorkflowId = await SubmitAsync(client, "alice");
        var jobId = await StartAsync(client, workflowId, "alice");

        var crossed = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/workflows/{otherWorkflowId}/runs/{jobId}/stream", null, "alice"),
            HttpCompletionOption.ResponseHeadersRead);

        crossed.StatusCode.Should().Be(HttpStatusCode.NotFound);

        _release.Release();
    }
}
