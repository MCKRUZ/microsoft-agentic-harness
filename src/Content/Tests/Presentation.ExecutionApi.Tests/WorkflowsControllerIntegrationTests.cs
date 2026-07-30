using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Presentation.ExecutionApi.Tests.WorkflowRequests;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Full-stack HTTP tests for the workflow-submission endpoint, hosting the real composition via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>. These prove the whole path — DI composition, auth,
/// the knowledge-scope middleware, the admission validator running as a MediatR pipeline behavior, the
/// mapper, and the plan store — is wired end to end, which no unit test can establish.
/// </summary>
public sealed class WorkflowsControllerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WorkflowsControllerIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }


    /// <summary>
    /// Serializes environment-variable overrides across factory startups, matching the pattern in
    /// <see cref="KnowledgeScopePipelineTests"/>: environment variables are the only default
    /// configuration source that both outranks <c>appsettings.json</c> and is visible to the eager
    /// <c>builder.Configuration</c> read inside <c>AddExecutionApiServices</c>. <c>UseSetting</c> and
    /// <c>ConfigureAppConfiguration</c> both apply too late for it.
    /// </summary>
    private static readonly object EnvironmentLock = new();

    /// <summary>
    /// Boots a host with <paramref name="variables"/> applied, forcing startup while they are visible
    /// so the eager configuration read observes them, then clearing them again.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactoryWith(Dictionary<string, string?> variables)
    {
        lock (EnvironmentLock)
        {
            foreach (var (key, value) in variables)
                Environment.SetEnvironmentVariable(key, value);

            var factory = new WebApplicationFactory<Program>();
            try
            {
                _ = factory.Server; // Force startup while the overrides are visible.
                return factory;
            }
            catch
            {
                factory.Dispose();
                throw;
            }
            finally
            {
                foreach (var key in variables.Keys)
                    Environment.SetEnvironmentVariable(key, null);
            }
        }
    }

    [Fact]
    public async Task Submit_ValidWorkflow_IsStoredAndReturnsTheMintedIdentifiers()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/workflows", Definition([LlmStep("draft"), LlmStep("review")]));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("workflowId").GetGuid().Should().NotBeEmpty();
        body.GetProperty("name").GetString().Should().Be("test-workflow");

        var stepIds = body.GetProperty("stepIds");
        stepIds.GetProperty("draft").GetGuid().Should().NotBeEmpty();
        stepIds.GetProperty("review").GetGuid().Should().NotBeEmpty();
        stepIds.GetProperty("draft").GetGuid().Should().NotBe(stepIds.GetProperty("review").GetGuid());

        // The Location header must name the resource the caller was just told about, so a client can
        // follow it without reassembling the URL from the body.
        response.Headers.Location!.ToString()
            .Should().EndWith(body.GetProperty("workflowId").GetGuid().ToString());
    }

    [Fact]
    public async Task Submit_TwoIdenticalDefinitions_ReceiveDistinctIdentifiers()
    {
        // Every identifier is minted server-side. Resubmitting the same body must not resolve to, or
        // collide with, the previously stored workflow.
        var client = _factory.CreateClient();
        var definition = Definition([LlmStep("only")]);

        var first = await client.PostAsJsonAsync("/api/workflows", definition);
        var second = await client.PostAsJsonAsync("/api/workflows", definition);

        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workflowId").GetGuid();
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workflowId").GetGuid();

        firstId.Should().NotBe(secondId);
    }

    [Fact]
    public async Task Submit_DuplicateStepNames_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/workflows", Definition([LlmStep("same"), LlmStep("same")]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_StepTypeDisagreeingWithItsConfigurationDiscriminator_Returns400()
    {
        // The two statements of the step's kind arrive over the wire independently, so this is the
        // only place the disagreement can be observed as a caller would produce it.
        var client = _factory.CreateClient();
        var mislabelled = new
        {
            name = "confused",
            type = "HumanGate",
            configuration = new { type = "tool_use", toolName = "file_system" }
        };

        var response = await client.PostAsJsonAsync("/api/workflows", Definition([mislabelled]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_ConditionalBranchWithTwoTrueArms_Returns400()
    {
        var client = _factory.CreateClient();
        var branch = new
        {
            name = "branch",
            type = "ConditionalBranch",
            configuration = new { type = "conditional_branch", conditionExpression = "score > 5" }
        };

        var response = await client.PostAsJsonAsync("/api/workflows", Definition(
            [branch, LlmStep("a"), LlmStep("b"), LlmStep("c")],
            [
                new { from = "branch", to = "a", type = "ConditionalTrue" },
                new { from = "branch", to = "b", type = "ConditionalTrue" },
                new { from = "branch", to = "c", type = "ConditionalFalse" }
            ]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_ConditionalBranchWithOneArmEach_IsAccepted()
    {
        var client = _factory.CreateClient();
        var branch = new
        {
            name = "branch",
            type = "ConditionalBranch",
            configuration = new { type = "conditional_branch", conditionExpression = "score > 5" }
        };

        var response = await client.PostAsJsonAsync("/api/workflows", Definition(
            [branch, LlmStep("approve"), LlmStep("reject")],
            [
                new { from = "branch", to = "approve", type = "ConditionalTrue" },
                new { from = "branch", to = "reject", type = "ConditionalFalse" }
            ]));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Submit_ReferencingAChildWorkflowThatDoesNotExist_Returns400()
    {
        var client = _factory.CreateClient();
        var subPlan = new
        {
            name = "child",
            type = "SubPlanInvocation",
            configuration = new { type = "sub_plan", childWorkflowId = Guid.NewGuid() }
        };

        var response = await client.PostAsJsonAsync("/api/workflows", Definition([subPlan]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_ReferencingAChildTheCallerJustSubmitted_IsAccepted()
    {
        // Proves the admission-time child resolution reads through the same scope-filtered store the
        // submission wrote to, rather than rejecting every reference outright.
        var client = _factory.CreateClient();

        var parent = await client.PostAsJsonAsync("/api/workflows", Definition([LlmStep("child-step")]));
        var childId = (await parent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workflowId").GetGuid();

        var response = await client.PostAsJsonAsync("/api/workflows", Definition(
            [
                new
                {
                    name = "invoke-child",
                    type = "SubPlanInvocation",
                    configuration = new { type = "sub_plan", childWorkflowId = childId }
                }
            ]));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Submit_WhenTheSubsystemIsDisabled_Returns403AndTheRouteStaysMounted()
    {
        // Off by default is the shipped posture, so a host that has not opted in must refuse rather
        // than 404 — a 404 would read as "this build has no such feature" and send an operator
        // looking for the wrong thing.
        using var disabled = CreateFactoryWith(new Dictionary<string, string?>
        {
            ["AppConfig__AI__WorkflowSubmission__Enabled"] = "false"
        });

        var client = disabled.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows", Definition([LlmStep("only")]));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Submit_WithAnOperatorTightenedStepCap_RejectsWorkflowsAboveIt()
    {
        // Proves the cap an operator configures is the cap actually enforced, rather than a constant
        // baked into the validator. It deliberately does NOT claim to prove live reload: this boots a
        // fresh host, which is a restart, so IOptionsMonitor's reload behaviour is out of its reach.
        using var tightened = CreateFactoryWith(new Dictionary<string, string?>
        {
            ["AppConfig__AI__WorkflowSubmission__MaxSteps"] = "1"
        });

        var client = tightened.CreateClient();

        var accepted = await client.PostAsJsonAsync("/api/workflows", Definition([LlmStep("one")]));
        var rejected = await client.PostAsJsonAsync(
            "/api/workflows", Definition([LlmStep("one"), LlmStep("two")]));

        accepted.StatusCode.Should().Be(HttpStatusCode.Created);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A host with <paramref name="variables"/> applied <em>and</em> the header-identity scheme
    /// mounted, so a test can be a specific caller.
    /// </summary>
    /// <remarks>
    /// Both halves have to happen inside one lock. The environment overrides are only visible during
    /// the eager configuration read at startup, and <c>WithWebHostBuilder</c> returns a factory that
    /// has not started yet — so applying them separately would boot the identity-bearing host after the
    /// variables had already been cleared.
    /// </remarks>
    private static WebApplicationFactory<Program> CreateIdentityFactoryWith(Dictionary<string, string?> variables)
    {
        lock (EnvironmentLock)
        {
            foreach (var (key, value) in variables)
                Environment.SetEnvironmentVariable(key, value);

            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureServices(services => services
                    .AddAuthentication(HeaderIdentityAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthenticationHandler>(
                        HeaderIdentityAuthenticationHandler.SchemeName, _ => { })));
            try
            {
                _ = factory.Server; // Force startup while the overrides are visible.
                return factory;
            }
            catch
            {
                factory.Dispose();
                throw;
            }
            finally
            {
                foreach (var key in variables.Keys)
                    Environment.SetEnvironmentVariable(key, null);
            }
        }
    }

    /// <summary>A client acting as <c>dave@contoso.com</c>, whose owner id is a different string.</summary>
    private static HttpClient DaveClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderIdentityAuthenticationHandler.UserHeader, "dave-oid");
        client.DefaultRequestHeaders.Add(HeaderIdentityAuthenticationHandler.ApproverHeader, "dave@contoso.com");
        return client;
    }

    [Fact]
    public async Task Submit_HumanGate_WhenTheHostNamesNoApprovers_IsRefused()
    {
        // The shipped default. Nothing in the request is wrong — the gate is well-formed, names a
        // plausible person, and the caller has a perfectly good identity — but a host that has not said
        // who may approve things has said that nothing may be approved.
        using var host = CreateIdentityFactoryWith([]);
        using var client = DaveClient(host);

        var response = await client.PostAsJsonAsync(
            "/api/workflows", Definition([GateStep("gate", "alice@contoso.com")]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_HumanGate_NamingAPermittedApprover_IsAccepted()
    {
        // The end-to-end proof that the controller actually resolves the submitter's approver identity
        // from the token and hands it to the validator. Everything else about this rule is unit-tested;
        // what no unit test can establish is that the wiring exists at all — and its absence would
        // refuse every gate ever submitted, on a host whose operator had configured a roster.
        using var host = CreateIdentityFactoryWith(new Dictionary<string, string?>
        {
            ["AppConfig__AI__WorkflowSubmission__PermittedApprovers__0"] = "alice@contoso.com"
        });

        using var client = DaveClient(host);

        var response = await client.PostAsJsonAsync(
            "/api/workflows", Definition([GateStep("gate", "alice@contoso.com")]));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Submit_HumanGate_NamingItsOwnSubmitter_IsRefused()
    {
        // The same host and the same caller as the accepted case above; the only difference is that
        // the gate names the caller. A gate its author can answer is not an approval, and the identity
        // being compared comes from the token — which is what this proves, since nothing in the request
        // body says who is submitting.
        using var host = CreateIdentityFactoryWith(new Dictionary<string, string?>
        {
            ["AppConfig__AI__WorkflowSubmission__PermittedApprovers__0"] = "alice@contoso.com",
            ["AppConfig__AI__WorkflowSubmission__PermittedApprovers__1"] = "dave@contoso.com"
        });

        using var client = DaveClient(host);

        var response = await client.PostAsJsonAsync(
            "/api/workflows", Definition([GateStep("gate", "alice@contoso.com", "dave@contoso.com")]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_WithoutAuthentication_IsRefused()
    {
        // The whole surface is [Authorize]. Under the shipped Development config the host runs the
        // anonymous scheme, so this asserts the endpoint is not reachable by an explicitly rejected
        // principal rather than that anonymous access fails.
        using var authenticated = CreateFactoryWith(new Dictionary<string, string?>
        {
            ["AppConfig__AI__BundleExecution__Auth__TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["AppConfig__AI__BundleExecution__Auth__ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["AppConfig__AI__BundleExecution__Auth__AllowAnonymous"] = "false"
        });

        var client = authenticated.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows", Definition([LlmStep("only")]));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Submit_CyclicWorkflow_IsRejectedAtSubmissionRatherThanAtFirstRun()
    {
        // The wire contract states PlanValidator enforces cycles for submissions. Only an end-to-end
        // request proves that validator is actually reached: every wire-level rule passes here, so
        // without the handler calling it this returns 201 and the cycle surfaces on first execution,
        // reported to whoever ran the workflow instead of whoever wrote it.
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows", Definition(
            [LlmStep("alpha"), LlmStep("beta")],
            [
                new { from = "alpha", to = "beta", type = "ControlFlow" },
                new { from = "beta", to = "alpha", type = "ControlFlow" }
            ]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_WithANullStepElement_Returns400NotAServerError()
    {
        // MVC's implicit-required rejects "steps": null but says nothing about "steps": [null] —
        // element nullability is not enforced by model binding — and FluentValidation keeps evaluating
        // rules after one fails. So the first predicate to touch the element used to throw, turning a
        // malformed body into a 500 from the component whose whole job is to answer 400.
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/workflows", new { name = "w", steps = new object?[] { null }, edges = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_WithANullEdgeElement_Returns400NotAServerError()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/workflows", new { name = "w", steps = new[] { LlmStep("a") }, edges = new object?[] { null } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_WithAMixOfRealAndNullSteps_Returns400NotAServerError()
    {
        // The per-step rules run over the surviving elements; the null must not reach them.
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            name = "w",
            steps = new object?[] { LlmStep("real"), null },
            edges = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_ConditionTheBranchEvaluatorWouldRefuse_IsRejectedAtSubmission()
    {
        // Admission and the executor must agree on what a condition may contain. A dotted expression is
        // refused by ConditionalBranchStepExecutor, so admitting it would store a workflow that looks
        // healthy and fails at the branch on first run — telling the runner, not the author.
        var client = _factory.CreateClient();
        var branch = new
        {
            name = "branch",
            type = "ConditionalBranch",
            configuration = new { type = "conditional_branch", conditionExpression = "score.value > 5" }
        };

        var response = await client.PostAsJsonAsync("/api/workflows", Definition(
            [branch, LlmStep("approve"), LlmStep("reject")],
            [
                new { from = "branch", to = "approve", type = "ConditionalTrue" },
                new { from = "branch", to = "reject", type = "ConditionalFalse" }
            ]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
