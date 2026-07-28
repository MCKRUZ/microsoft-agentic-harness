using System.Net;
using System.Net.Http.Json;
using Application.Core.CQRS.DriftDetection;
using Domain.AI.DriftDetection;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Presentation.Common.Drift;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Wire-level tests for the drift API mounted into AgentHub via <c>AddDriftApi()</c>: the
/// routes exist (opt-in wiring works end-to-end through the real host), role gating fails
/// closed in both directions (the read role cannot write and the operate role cannot read),
/// and the caller identity on writes comes from the token's <c>oid</c> claim — a spoofed
/// caller-id field in the request body is ignored because no DTO binds it.
/// </summary>
public sealed class DriftControllerIntegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public DriftControllerIntegrationTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateAuthedClient(string userId = "alice@contoso.com", string? roles = null)
    {
        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { })))
            .CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static object CreateEvaluationBody() => new
    {
        scope = "Skill",
        scopeIdentifier = "summarize",
        dimensions = new Dictionary<string, double> { ["Faithfulness"] = 0.8 }
    };

    [Fact]
    public async Task GetBaselines_Unauthenticated_Returns401()
    {
        // Also proves the routes are mounted at all: an unmounted route would 404, not challenge.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/drift/baselines");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetBaselines_AuthenticatedWithoutReadRole_Returns403()
    {
        using var client = CreateAuthedClient();

        var response = await client.GetAsync("/api/drift/baselines");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "role gating must fail closed for authenticated callers without Harness.Drift.Read");
    }

    [Fact]
    public async Task GetBaselines_WithOperateRoleOnly_Returns403()
    {
        using var client = CreateAuthedClient(roles: DriftController.OperateRole);

        var response = await client.GetAsync("/api/drift/baselines");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "role separation runs both ways: the ops write role must not imply read access");
    }

    [Fact]
    public async Task GetBaselines_WithReadRole_Returns200()
    {
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<GetDriftBaselinesQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<DriftBaseline>>.Success([]));

        using var client = CreateAuthedClient(roles: DriftController.ReadRole);

        var response = await client.GetAsync("/api/drift/baselines");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetHistory_OmittedScope_Returns400NotAgentScopedData()
    {
        // DriftScope.Agent is the enum's zero value, so a non-nullable binding would have
        // silently answered for Agent scope with a 200 while the caller believed they had
        // asked for something else. A missing required filter must be a 400.
        using var client = CreateAuthedClient(roles: DriftController.ReadRole);

        var response = await client.GetAsync(
            "/api/drift/history?scopeIdentifier=summarize" +
            "&start=2026-07-01T00:00:00Z&end=2026-07-27T00:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an omitted scope must be rejected, never defaulted to Agent");
    }

    [Fact]
    public async Task GetHistory_WithScope_Returns200()
    {
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<GetDriftHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<DriftScore>>.Success([]));

        using var client = CreateAuthedClient(roles: DriftController.ReadRole);

        var response = await client.GetAsync(
            "/api/drift/history?scope=Skill&scopeIdentifier=summarize" +
            "&start=2026-07-01T00:00:00Z&end=2026-07-27T00:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an explicit scope is the supported way to read history");
    }

    [Fact]
    public async Task PushEvaluation_WithReadRoleOnly_Returns403()
    {
        using var client = CreateAuthedClient(roles: DriftController.ReadRole);

        var response = await client.PostAsJsonAsync("/api/drift/evaluations", CreateEvaluationBody());

        // No Times.Never verify here: MockMediator is fixture-shared across this class, so
        // other tests' legitimate sends would pollute the count. The 403 itself proves the
        // request never reached the pipeline for THIS call — MVC rejects before the action runs.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a reader must never be able to push evaluations — that is the poisoning boundary");
    }

    [Fact]
    public async Task PushEvaluation_WithOperateRole_StampsCallerFromTokenAndIgnoresBodyField()
    {
        PushDriftEvaluationCommand? captured = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<PushDriftEvaluationCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<DriftScore>>, CancellationToken>(
                (c, _) => captured = (PushDriftEvaluationCommand)c)
            .ReturnsAsync(Result<DriftScore>.Success(new DriftScore
            {
                ScoreId = Guid.NewGuid(),
                BaselineId = Guid.NewGuid(),
                Scope = DriftScope.Skill,
                ScopeIdentifier = "summarize",
                Dimensions = new Dictionary<DriftDimension, DriftDimensionScore>(),
                OverallDrift = 0.2,
                Severity = DriftSeverity.None,
                ScoredAt = DateTimeOffset.UtcNow
            }));

        using var client = CreateAuthedClient("ops@contoso.com", DriftController.OperateRole);

        // The spoofed callerId member has no binding target on the DTO and must be ignored.
        var response = await client.PostAsJsonAsync("/api/drift/evaluations", new
        {
            scope = "Skill",
            scopeIdentifier = "summarize",
            dimensions = new Dictionary<string, double> { ["Faithfulness"] = 0.8 },
            callerId = "mallory@evil.example"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.CallerId.Should().Be("ops@contoso.com",
            "the caller identity must come from the authenticated principal's oid claim, never the body");
        captured.Dimensions.Should().ContainKey(DriftDimension.Faithfulness);
    }

    [Fact]
    public async Task RecalculateBaseline_WithOperateRole_Returns200AndStampsCaller()
    {
        var baselineId = Guid.NewGuid();
        RecalculateDriftBaselineCommand? captured = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<RecalculateDriftBaselineCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<DriftBaseline>>, CancellationToken>(
                (c, _) => captured = (RecalculateDriftBaselineCommand)c)
            .ReturnsAsync(Result<DriftBaseline>.Success(new DriftBaseline
            {
                BaselineId = Guid.NewGuid(),
                Scope = DriftScope.Skill,
                ScopeIdentifier = "summarize",
                Dimensions = new Dictionary<DriftDimension, double>(),
                DimensionSigmas = new Dictionary<DriftDimension, double>(),
                SampleCount = 25,
                WindowStart = DateTimeOffset.UtcNow.AddDays(-7),
                WindowEnd = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            }));

        using var client = CreateAuthedClient("ops@contoso.com", DriftController.OperateRole);

        var response = await client.PostAsync($"/api/drift/baselines/{baselineId}/recalculate", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.BaselineId.Should().Be(baselineId);
        captured.CallerId.Should().Be("ops@contoso.com");
    }

    [Fact]
    public async Task RecalculateBaseline_UnknownId_Returns404()
    {
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<RecalculateDriftBaselineCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<DriftBaseline>.NotFound("No baseline with the given id."));

        using var client = CreateAuthedClient("ops@contoso.com", DriftController.OperateRole);

        var response = await client.PostAsync($"/api/drift/baselines/{Guid.NewGuid()}/recalculate", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an unknown baseline id maps through the shared failure mapper to 404");
    }

    [Fact]
    public async Task RecalculateBaseline_WithReadRoleOnly_Returns403()
    {
        using var client = CreateAuthedClient(roles: DriftController.ReadRole);

        var response = await client.PostAsync($"/api/drift/baselines/{Guid.NewGuid()}/recalculate", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "recalculation re-anchors 'normal' — an operator power the read role must not grant");
    }
}
