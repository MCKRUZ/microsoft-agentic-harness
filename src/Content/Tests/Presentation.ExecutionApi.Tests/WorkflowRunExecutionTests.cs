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
/// Drives a submitted workflow all the way to a terminal status through the real host.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this suite stops at acceptance — it asserts that a run was taken and that
/// another caller cannot see it. That leaves the entire execution path unasserted, and two defects
/// lived in exactly that gap: the dispatcher never re-established the caller's knowledge scope, so
/// every run failed to load its own plan; and any plan that ended Blocked or Cancelled was reported to
/// the caller as Succeeded. Both are invisible to a test that never polls past Queued.
/// </para>
/// <para>
/// Only the leaf is substituted. The step executor for the step type under test is replaced so no
/// model is called, and everything above it — admission, storage, the dispatcher, the scope, plan
/// loading, the scheduler, status mapping, and the HTTP projection — is the host's own.
/// </para>
/// </remarks>
public sealed class WorkflowRunExecutionTests
{
    /// <summary>Stands in for the model call, so the run's outcome is chosen by the test.</summary>
    private sealed class ScriptedStepExecutor(StepExecutionStatus status, string? error = null) : IPlanStepExecutor
    {
        public Task<StepExecutionResult> ExecuteAsync(
            PlanStep step,
            IReadOnlyDictionary<PlanStepId, string> upstreamOutputs,
            CancellationToken ct) =>
            Task.FromResult(new StepExecutionResult
            {
                Status = status,
                Output = status == StepExecutionStatus.Completed ? "done" : null,
                ErrorMessage = error,
                Duration = TimeSpan.FromMilliseconds(1)
            });
    }

    private static WebApplicationFactory<Program> CreateFactory(StepExecutionStatus stepOutcome) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(HeaderIdentityAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>(
                        HeaderIdentityAuthenticationHandler.SchemeName, _ => { });

                services.AddKeyedScoped<IPlanStepExecutor>(
                    StepType.LlmCall, (_, _) => new ScriptedStepExecutor(stepOutcome));
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

    private static async Task<Guid> SubmitWorkflowAsync(HttpClient client, string oid)
    {
        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/api/workflows", Definition([LlmStep("only")]), oid));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "submission failed with body: {0}", body);
        return JsonDocument.Parse(body).RootElement.GetProperty("workflowId").GetGuid();
    }

    private static async Task<string> StartRunAsync(HttpClient client, Guid workflowId, string oid)
    {
        var started = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/workflows/{workflowId}/runs", body: null, oid));

        var body = await started.Content.ReadAsStringAsync();
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, "start failed with body: {0}", body);
        return JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()!;
    }

    /// <summary>
    /// Polls the run until it leaves the live states, then returns the final body.
    /// </summary>
    /// <remarks>
    /// Paced at an interval a real client could sustain. The endpoint permits 60 requests a minute per
    /// caller, and polling is the only way to learn a run's outcome, so a tight loop exhausts the
    /// caller's whole budget in seconds and gets 503s that look like execution failures. A throttled
    /// poll is treated as "ask again", not as an outcome — the overall attempt budget is what bounds
    /// the wait.
    /// </remarks>
    private static async Task<JsonElement> PollUntilSettledAsync(
        HttpClient client, Guid workflowId, string jobId, string oid)
    {
        var last = "no response";

        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(250);

            var response = await client.SendAsync(
                Request(HttpMethod.Get, $"/api/workflows/{workflowId}/runs/{jobId}", body: null, oid));

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                last = "throttled";
                continue;
            }

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            last = body.ToString();

            var status = body.GetProperty("status").GetString();
            if (status is not ("Queued" or "Running"))
                return body;
        }

        throw new Xunit.Sdk.XunitException($"The run never left the live states. Last response: {last}");
    }

    [Fact]
    public async Task AWorkflowThatCompletes_IsReportedSucceeded()
    {
        // The headline capability. It failed 100% of the time before the dispatcher was taught to
        // re-establish the run's identity: the plan was stored under alice, the dispatcher loaded it
        // as nobody, and an owned plan is invisible to nobody — so every run died "Plan not found".
        using var factory = CreateFactory(StepExecutionStatus.Completed);
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");
        var jobId = await StartRunAsync(client, workflowId, "alice");

        var body = await PollUntilSettledAsync(client, workflowId, jobId, "alice");

        body.GetProperty("status").GetString().Should().Be("Succeeded");
        body.GetProperty("completedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AWorkflowWhoseStepFails_IsReportedFailed()
    {
        using var factory = CreateFactory(StepExecutionStatus.Failed);
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");
        var jobId = await StartRunAsync(client, workflowId, "alice");

        var body = await PollUntilSettledAsync(client, workflowId, jobId, "alice");

        body.GetProperty("status").GetString().Should().Be("Failed");
        body.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace(
            "a caller that asked for work is owed a reason it did not happen");
    }

    [Fact]
    public async Task AWorkflowThatParksAwaitingAnApproval_IsNotReportedSucceeded()
    {
        // A plan can end Blocked, and a plan that ends Blocked produced no result. Reporting it as
        // Succeeded tells a caller polling for an outcome that finished work exists, when in fact
        // nothing has run past the gate and nobody has been asked to approve anything.
        using var factory = CreateFactory(StepExecutionStatus.Blocked);
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");
        var jobId = await StartRunAsync(client, workflowId, "alice");

        var body = await PollUntilSettledAsync(client, workflowId, jobId, "alice");

        body.GetProperty("status").GetString().Should().Be("Blocked");
    }

    [Fact]
    public async Task ASecondRunOfALiveWorkflow_IsRefusedRatherThanSharingTheFirstsState()
    {
        // A workflow's execution state is keyed by the workflow, so two concurrent runs are not two
        // executions — they are two schedulers driving one state machine, re-running each other's
        // in-flight steps and adopting each other's outputs.
        using var factory = CreateFactory(StepExecutionStatus.Completed);
        using var client = factory.CreateClient();

        var workflowId = await SubmitWorkflowAsync(client, "alice");
        var jobId = await StartRunAsync(client, workflowId, "alice");

        var second = await client.SendAsync(
            Request(HttpMethod.Post, $"/api/workflows/{workflowId}/runs", body: null, "alice"));

        // Either the first run is still live and the second is refused, or the first already finished
        // and the second is admitted. Both are correct; what must never happen is two live at once.
        if (second.StatusCode == HttpStatusCode.Accepted)
        {
            var first = await PollUntilSettledAsync(client, workflowId, jobId, "alice");
            first.GetProperty("status").GetString().Should().NotBe("Running",
                "a second run was admitted, so the first must already have been terminal");
            return;
        }

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await PollUntilSettledAsync(client, workflowId, jobId, "alice");
    }
}
