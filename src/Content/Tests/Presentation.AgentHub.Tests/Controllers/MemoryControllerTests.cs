using Application.Core.CQRS.Memory;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Presentation.AgentHub.Controllers;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

/// <summary>
/// Direct controller unit tests — no WebApplicationFactory. Verifies the
/// <see cref="MemoryController"/>'s <c>Result</c> → MVC status-code mapping, the honest
/// outcome projection on writes, and that each endpoint dispatches the correct
/// command/query through MediatR.
/// </summary>
/// <remarks>
/// Wire-level (auth, routing, middleware, rate limiting) coverage lives in the existing
/// AgentHub WebApplicationFactory infrastructure; the factory replaces <c>IMediator</c> with a
/// mock, so handler behavior itself is covered by the Application.Core.Tests handler suite.
/// </remarks>
public sealed class MemoryControllerTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly MemoryController _sut;

    public MemoryControllerTests()
    {
        _sut = new MemoryController(_mediator.Object);
    }

    // --- Remember ---

    [Fact]
    public async Task Remember_GatePersists_ReturnsOkWithHonestOutcome()
    {
        RememberMemoryCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<RememberMemoryCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<RememberMemoryResult>>, CancellationToken>(
                (c, _) => captured = (RememberMemoryCommand)c)
            .ReturnsAsync(Result<RememberMemoryResult>.Success(new RememberMemoryResult
            {
                Outcome = MemoryWriteOutcome.Quarantined,
                Reason = "quarantined: DirectOverride"
            }));

        var result = await _sut.Remember(
            new RememberMemoryRequest("favorite-color", "blue", "Preference"),
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<RememberMemoryResponse>().Subject;
        response.Outcome.Should().Be("Quarantined", "the gate outcome must be surfaced as its enum name, not masked");
        response.Reason.Should().Be("quarantined: DirectOverride");

        captured.Should().NotBeNull();
        captured!.Key.Should().Be("favorite-color");
        captured.Content.Should().Be("blue");
        captured.EntityType.Should().Be("Preference");
    }

    [Fact]
    public async Task Remember_EntityTypeOmitted_DefaultsToFact()
    {
        RememberMemoryCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<RememberMemoryCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<RememberMemoryResult>>, CancellationToken>(
                (c, _) => captured = (RememberMemoryCommand)c)
            .ReturnsAsync(Result<RememberMemoryResult>.Success(new RememberMemoryResult
            {
                Outcome = MemoryWriteOutcome.Persisted,
                Reason = "trusted"
            }));

        await _sut.Remember(new RememberMemoryRequest("k1", "content"), CancellationToken.None);

        captured!.EntityType.Should().Be("Fact");
    }

    [Fact]
    public async Task Remember_ValidationFailure_Returns400()
    {
        _mediator.Setup(m => m.Send(It.IsAny<RememberMemoryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<RememberMemoryResult>.ValidationFailure(["Key may only contain letters..."]));

        var result = await _sut.Remember(
            new RememberMemoryRequest("bad:key", "content"), CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Remember_UnexpectedFailure_Returns500WithoutInternalDetails()
    {
        // Security guard: store exceptions can contain connection strings or file paths.
        // Per the harness security rules, General failures must map to a generic body.
        const string sensitive = "Neo4j.Driver.ServiceUnavailableException at bolt://10.0.0.5:7687";
        _mediator.Setup(m => m.Send(It.IsAny<RememberMemoryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<RememberMemoryResult>.Fail(sensitive));

        var result = await _sut.Remember(
            new RememberMemoryRequest("k1", "content"), CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var details = problem.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        details.Detail.Should().NotContain("Neo4j", "raw store errors must not leak through MapFailure on General failures");
        details.Detail.Should().NotContain("10.0.0.5", "internal endpoints must not leak through MapFailure on General failures");
    }

    // --- Search ---

    [Fact]
    public async Task Search_ValidQuery_ReturnsOkWithEntriesAndPassesParameters()
    {
        RecallMemoryQuery? captured = null;
        IReadOnlyList<MemoryEntry> entries =
        [
            new MemoryEntry { Key = "favorite-color", Content = "blue", EntityType = "Preference" }
        ];
        _mediator.Setup(m => m.Send(It.IsAny<RecallMemoryQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<IReadOnlyList<MemoryEntry>>>, CancellationToken>(
                (q, _) => captured = (RecallMemoryQuery)q)
            .ReturnsAsync(Result<IReadOnlyList<MemoryEntry>>.Success(entries));

        var result = await _sut.Search("color", 7, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(entries);
        captured!.Query.Should().Be("color");
        captured.MaxResults.Should().Be(7);
    }

    [Fact]
    public async Task Search_MissingQuery_Returns400()
    {
        _mediator.Setup(m => m.Send(It.IsAny<RecallMemoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<MemoryEntry>>.ValidationFailure(["Query must not be empty."]));

        var result = await _sut.Search(null, 5, CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // --- Forget ---

    [Fact]
    public async Task Forget_ExistingOrMissingKey_ReturnsNoContent()
    {
        ForgetMemoryCommand? captured = null;
        _mediator.Setup(m => m.Send(It.IsAny<ForgetMemoryCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result>, CancellationToken>((c, _) => captured = (ForgetMemoryCommand)c)
            .ReturnsAsync(Result.Success());

        var result = await _sut.Forget("favorite-color", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        captured!.Key.Should().Be("favorite-color");
    }

    [Fact]
    public async Task Forget_ValidationFailure_Returns400()
    {
        _mediator.Setup(m => m.Send(It.IsAny<ForgetMemoryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.ValidationFailure(["Key may only contain letters..."]));

        var result = await _sut.Forget("bad key", CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
