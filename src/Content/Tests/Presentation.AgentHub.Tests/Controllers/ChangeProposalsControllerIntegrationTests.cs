using System.Net;
using System.Net.Http.Json;
using Application.AI.Common.CQRS.Changes.ApproveChangeProposal;
using Application.AI.Common.CQRS.Changes.CancelChangeProposal;
using Application.AI.Common.CQRS.Changes.ListChangeProposals;
using Domain.AI.Changes;
using Domain.AI.Identity;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Presentation.Common.ChangeProposals;
using Xunit;
using EditOp = Domain.AI.SkillTraining.EditOp;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Wire-level tests for the change-proposal decision API mounted into AgentHub via
/// <c>AddChangeProposalApi()</c>: the routes exist (opt-in wiring works end-to-end through the
/// real host), role gating fails closed, cancel is admin-only, and the reviewer identity comes
/// from the token's <c>preferred_username</c> claim — a spoofed reviewer-id field in the request
/// body is ignored because no DTO binds it, so the audit history can only ever name the
/// authenticated caller.
/// </summary>
public sealed class ChangeProposalsControllerIntegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ChangeProposalsControllerIntegrationTests(TestWebApplicationFactory factory) => _factory = factory;

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

    private static ChangeProposal NewProposal(ChangeProposalStatus status) =>
        ChangeProposal.Create(
            target: new GitRepoTarget("https://github.com/org/repo", "main", "abc123"),
            diff: [new ChangeEdit { Op = EditOp.Replace, Target = "foo", Content = "bar" }],
            submittedBy: new AgentIdentity { Id = "agent-001", Kind = AgentIdentityKind.ManagedIdentity },
            summary: "rename foo to bar",
            blastRadius: BlastRadius.Low,
            requiredGates: ["self_validation", "approval", "merge"],
            submittedAt: new DateTimeOffset(2026, 6, 6, 10, 30, 15, TimeSpan.Zero)) with
        {
            Status = status
        };

    [Fact]
    public async Task GetProposals_Unauthenticated_Returns401()
    {
        // Also proves the routes are mounted at all: an unmounted route would 404, not challenge.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/change-proposals");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetProposals_AuthenticatedWithoutDecideRole_Returns403()
    {
        using var client = CreateAuthedClient();

        var response = await client.GetAsync("/api/change-proposals");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "role gating must fail closed for authenticated callers without Harness.Proposals.Decide");
    }

    [Fact]
    public async Task GetProposals_WithDecideRole_Returns200()
    {
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<ListChangeProposalsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<ChangeProposal>>.Success([]));

        using var client = CreateAuthedClient("bob@contoso.com", ChangeProposalsController.DecideRole);

        var response = await client.GetAsync("/api/change-proposals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Approve_BodyReviewerIdField_IsIgnoredTokenWins()
    {
        ApproveChangeProposalCommand? captured = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<ApproveChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ChangeProposal>>, CancellationToken>(
                (c, _) => captured = (ApproveChangeProposalCommand)c)
            .ReturnsAsync(Result<ChangeProposal>.Success(NewProposal(ChangeProposalStatus.Approved)));

        using var client = CreateAuthedClient("alice@contoso.com", ChangeProposalsController.DecideRole);

        // The spoofed reviewerId member has no binding target on the DTO and must be ignored.
        var response = await client.PostAsJsonAsync(
            "/api/change-proposals/some-proposal-id/approve",
            new { reason = "ok", reviewerId = "mallory@evil.example" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.ReviewerId.Should().Be("alice@contoso.com",
            "a body-supplied reviewer id must never influence the audit-stamped identity");
        captured.ProposalId.Should().Be("some-proposal-id");
        captured.Reason.Should().Be("ok");
    }

    [Fact]
    public async Task Cancel_WithDecideRoleOnly_Returns403()
    {
        using var client = CreateAuthedClient("alice@contoso.com", ChangeProposalsController.DecideRole);

        var response = await client.PostAsJsonAsync(
            "/api/change-proposals/some-proposal-id/cancel", new { reason = "stale" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "cancel is admin-only; the decide role must not suffice");
    }

    [Fact]
    public async Task Cancel_WithAdminRole_Returns200AndStampsCancelledByFromToken()
    {
        CancelChangeProposalCommand? captured = null;
        _factory.MockMediator
            .Setup(m => m.Send(It.IsAny<CancelChangeProposalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<ChangeProposal>>, CancellationToken>(
                (c, _) => captured = (CancelChangeProposalCommand)c)
            .ReturnsAsync(Result<ChangeProposal>.Success(NewProposal(ChangeProposalStatus.Cancelled)));

        using var client = CreateAuthedClient("ops@contoso.com", ChangeProposalsController.AdminRole);

        var response = await client.PostAsJsonAsync(
            "/api/change-proposals/some-proposal-id/cancel", new { reason = "superseded" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured!.CancelledBy.Should().Be("ops@contoso.com",
            "the cancelling identity must come from the authenticated principal's claim");
    }
}
