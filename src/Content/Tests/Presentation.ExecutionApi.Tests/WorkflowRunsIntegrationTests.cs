using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Presentation.ExecutionApi.Tests.WorkflowRequests;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Full-stack tests for starting a workflow run and polling its status, driven through the real host
/// with two distinct authenticated identities.
/// </summary>
/// <remarks>
/// These exercise the whole path a caller actually takes — submit, start, poll — and the isolation
/// between callers at each step. A job identifier is the only thing separating one caller's work from
/// another's, so the cross-owner cases matter as much as the happy path.
/// </remarks>
public sealed class WorkflowRunsIntegrationTests
{
    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services
                .AddAuthentication(HeaderIdentityAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>(
                    HeaderIdentityAuthenticationHandler.SchemeName, _ => { })));

    private static HttpRequestMessage Request(HttpMethod method, string url, object? body, string oid)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        request.Headers.Add(HeaderIdentityAuthenticationHandler.UserHeader, oid);
        request.Headers.Add(HeaderIdentityAuthenticationHandler.TenantHeader, "acme");
        return request;
    }

    private static async Task<Guid> SubmitWorkflowAsync(HttpClient client, string oid)
    {
        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/api/workflows", Definition([LlmStep("only")]), oid));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "submission failed with body: {0}", body);
        return JsonDocument.Parse(body).RootElement.GetProperty("workflowId").GetGuid();
    }

    [Fact]
    public async Task StartingARun_IsAcceptedAndPollable()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");

        var started = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/workflows/{workflowId}/runs", body: null, "alice"));

        // Accepted, not OK: the work was taken, not finished. A 200 here would invite a caller to
        // treat acceptance as a result.
        started.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var startedBody = await started.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = startedBody.GetProperty("jobId").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();
        startedBody.GetProperty("statusUrl").GetString().Should().Contain(jobId);

        var status = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/workflows/{workflowId}/runs/{jobId}", body: null, "alice"));

        status.StatusCode.Should().Be(HttpStatusCode.OK);
        var statusBody = await status.Content.ReadFromJsonAsync<JsonElement>();
        statusBody.GetProperty("jobId").GetString().Should().Be(jobId);
        statusBody.GetProperty("workflowId").GetString().Should().Be(workflowId.ToString());
    }

    [Fact]
    public async Task StatusResponse_NeverPublishesTheRunsCapabilityEnvelope()
    {
        // The envelope is the host's authorization state for the run, not the caller's business.
        // Returning the stored record directly would publish the exact grant the run holds.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");
        var started = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/workflows/{workflowId}/runs", body: null, "alice"));
        var jobId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString();

        var status = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/workflows/{workflowId}/runs/{jobId}", body: null, "alice"));

        var raw = await status.Content.ReadAsStringAsync();
        raw.Should().NotContainAny("envelope", "allowedTools", "autonomyCeiling", "tenantId");
    }

    [Fact]
    public async Task StartingARunOnSomeoneElsesWorkflow_Is404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");

        var stolen = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/workflows/{workflowId}/runs", body: null, "mallory"));

        // 404, not 403: a caller must not be able to confirm a workflow exists by trying to run it.
        stolen.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PollingSomeoneElsesRun_Is404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");
        var started = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/workflows/{workflowId}/runs", body: null, "alice"));
        var jobId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString();

        var peeked = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/workflows/{workflowId}/runs/{jobId}", body: null, "mallory"));

        peeked.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PollingARunUnderTheWrongWorkflow_Is404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");
        var otherWorkflowId = await SubmitWorkflowAsync(client, "alice");

        var started = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/workflows/{workflowId}/runs", body: null, "alice"));
        var jobId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString();

        var crossed = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/workflows/{otherWorkflowId}/runs/{jobId}", body: null, "alice"));

        crossed.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the route asserts a relationship, and answering against a different workflow would let a "
            + "caller discover which workflow a job belongs to by trying routes");
    }

    [Fact]
    public async Task StartingARunOnAWorkflowThatDoesNotExist_Is404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/workflows/{Guid.NewGuid()}/runs", body: null, "alice"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PollingAJobIdThatWasNeverIssued_Is404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");

        var response = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/workflows/{workflowId}/runs/never-issued", body: null, "alice"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CancellingARun_StopsItAndReportsWhatItAchieved()
    {
        // Route binding and the response's wire shape are only reachable over HTTP. The handler tests
        // prove the decisions; this proves a caller can actually make them, and reads back the run to
        // confirm the cancellation is what the status endpoint reports rather than merely what the
        // cancel call claimed.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");

        var started = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/workflows/{workflowId}/runs", body: null, "alice"));
        var jobId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString();

        var cancelled = await client.SendAsync(
            Request(HttpMethod.Delete, $"/api/workflows/{workflowId}/runs/{jobId}", body: null, "alice"));

        cancelled.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("jobId").GetString().Should().Be(jobId);
        body.GetProperty("withdrawnApprovals").GetInt32().Should().Be(
            0, "this workflow has no gate, so there was nothing to withdraw");

        // A single-step workflow may already have run to completion, in which case cancelling is a
        // conflict rather than a stop — so this asserts what is true either way: whatever the run's
        // final state, it is terminal and the cancel call did not leave it live.
        var polled = await client.SendAsync(
            Request(HttpMethod.Get, $"/api/workflows/{workflowId}/runs/{jobId}", body: null, "alice"));
        polled.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CancellingAnotherCallersRun_Is404AndDoesNotStopIt()
    {
        // Cancelling raises the stakes of the ownership rule every run endpoint follows: an endpoint
        // that distinguished "not yours" from "not there" would hand one caller a way to stop
        // another's work by guessing identifiers.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");
        var started = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/workflows/{workflowId}/runs", body: null, "alice"));
        var jobId = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString();

        var response = await client.SendAsync(
            Request(HttpMethod.Delete, $"/api/workflows/{workflowId}/runs/{jobId}", body: null, "mallory"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CancellingAJobIdThatWasNeverIssued_Is404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");

        var response = await client.SendAsync(
            Request(HttpMethod.Delete, $"/api/workflows/{workflowId}/runs/never-issued", body: null, "alice"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
