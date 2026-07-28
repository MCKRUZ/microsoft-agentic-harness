using System.Net;
using System.Net.Http.Json;
using Application.Core.CQRS.Autonomy;
using Domain.AI.Agents;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Presentation.Common.Governance;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Wire-level tests for the autonomy governance read API mounted into AgentHub via
/// <c>AddAutonomyApi()</c>: the routes exist (opt-in wiring works end-to-end through the real
/// host), role gating fails closed for callers without <c>Harness.Governance.Read</c>, route
/// and body values bind into the dispatched queries, and <c>NotFound</c> results map to 404.
/// </summary>
public sealed class AutonomyControllerIntegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AutonomyControllerIntegrationTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetTier_Unauthenticated_Returns401()
    {
        // Also proves the routes are mounted at all: an unmounted route would 404, not challenge.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/governance/autonomy/tiers/Explore");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetTier_AuthenticatedWithoutReadRole_Returns403()
    {
        using var client = _factory.CreateAuthedClient();

        var response = await client.GetAsync("/api/governance/autonomy/tiers/Explore");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "role gating must fail closed for authenticated callers without Harness.Governance.Read");
    }

    [Fact]
    public async Task GetTier_WithReadRole_Returns200AndBindsRouteValue()
    {
        GetAutonomyTierQuery? captured = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<GetAutonomyTierQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<AutonomyTierDetail>>, CancellationToken>(
                (q, _) => captured = (GetAutonomyTierQuery)q)
            .ReturnsAsync(Result<AutonomyTierDetail>.Success(
                new AutonomyTierDetail(SubagentType.Explore, AutonomyLevel.Supervised)));

        using var client = _factory.CreateAuthedClient("bob@contoso.com", AutonomyController.ReadRole);

        var response = await client.GetAsync("/api/governance/autonomy/tiers/Explore");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.SubagentType.Should().Be("Explore",
            "the route segment must flow into the dispatched query untouched");
    }

    [Fact]
    public async Task GetTier_UnknownSubagentType_Returns404()
    {
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<GetAutonomyTierQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<AutonomyTierDetail>.NotFound(
                "No subagent type with the given name exists."));

        using var client = _factory.CreateAuthedClient("bob@contoso.com", AutonomyController.ReadRole);

        var response = await client.GetAsync("/api/governance/autonomy/tiers/Nonexistent");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a NotFound result must map through the shared FailureResponse extension to 404");
    }

    [Fact]
    public async Task PreviewDecision_AuthenticatedWithoutReadRole_Returns403()
    {
        using var client = _factory.CreateAuthedClient();

        var response = await client.PostAsJsonAsync(
            "/api/governance/autonomy/decision-preview",
            new { subagentType = "Execute", blastRadius = "Medium" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PreviewDecision_WithReadRole_Returns200AndBindsBody()
    {
        PreviewAutonomyDecisionQuery? captured = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<PreviewAutonomyDecisionQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<AutonomyDecisionPreviewResult>>, CancellationToken>(
                (q, _) => captured = (PreviewAutonomyDecisionQuery)q)
            .ReturnsAsync(Result<AutonomyDecisionPreviewResult>.Success(
                new AutonomyDecisionPreviewResult(
                    SubagentType.Execute, AutonomyDecision.RequiresApproval,
                    AutonomyLevel.Supervised, BlastRadius.Medium, ChangeTargetKind.GitRepo,
                    true, "Development", "skill.demo", "tier baseline")));

        using var client = _factory.CreateAuthedClient("ops@contoso.com", AutonomyController.ReadRole);

        var response = await client.PostAsJsonAsync(
            "/api/governance/autonomy/decision-preview",
            new
            {
                subagentType = "Execute",
                blastRadius = "Medium",
                targetKind = "GitRepo",
                isStateChange = true,
                skillKey = "skill.demo",
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.SubagentType.Should().Be("Execute");
        captured.BlastRadius.Should().Be("Medium");
        captured.TargetKind.Should().Be("GitRepo");
        captured.IsStateChange.Should().BeTrue();
        captured.SkillKey.Should().Be("skill.demo");
    }

    [Fact]
    public async Task PreviewDecision_OmittedTargetKind_DefaultsToUnspecified()
    {
        PreviewAutonomyDecisionQuery? captured = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<PreviewAutonomyDecisionQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<AutonomyDecisionPreviewResult>>, CancellationToken>(
                (q, _) => captured = (PreviewAutonomyDecisionQuery)q)
            .ReturnsAsync(Result<AutonomyDecisionPreviewResult>.Success(
                new AutonomyDecisionPreviewResult(
                    SubagentType.Plan, AutonomyDecision.AutoApprove, AutonomyLevel.Autonomous,
                    BlastRadius.Trivial, ChangeTargetKind.Unspecified, false, "Development",
                    null, "trivial fallback")));

        using var client = _factory.CreateAuthedClient("ops@contoso.com", AutonomyController.ReadRole);

        var response = await client.PostAsJsonAsync(
            "/api/governance/autonomy/decision-preview",
            new { subagentType = "Plan", blastRadius = "Trivial" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.TargetKind.Should().Be(nameof(ChangeTargetKind.Unspecified),
            "an omitted target kind must evaluate as Unspecified, matching non-target-specific proposals");
    }
}
