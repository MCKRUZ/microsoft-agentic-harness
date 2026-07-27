using Domain.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Moq;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.Common.Tests.Extensions;

/// <summary>
/// Locks the canonical failure→ProblemDetails contract of
/// <see cref="ControllerBaseExtensions.FailureResponse"/>: every classified failure type maps to
/// its own status code with the result's errors as detail, and unclassified failures collapse to
/// an opaque 500 that never echoes the result's error text.
/// </summary>
public sealed class ControllerBaseExtensionsTests
{
    private sealed class ProbeController : ControllerBase;

    private static ProbeController CreateController()
    {
        var factory = new Mock<ProblemDetailsFactory>();
        factory
            .Setup(f => f.CreateProblemDetails(
                It.IsAny<HttpContext>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((HttpContext _, int? status, string? title, string? _, string? detail, string? _) =>
                new ProblemDetails { Status = status, Title = title, Detail = detail });

        return new ProbeController
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            ProblemDetailsFactory = factory.Object,
        };
    }

    private static ProblemDetails Payload(IActionResult response) =>
        response.Should().BeOfType<ObjectResult>().Which.Value
            .Should().BeOfType<ProblemDetails>().Subject;

    public static TheoryData<Result, int, string> ClassifiedFailures => new()
    {
        { Result.ValidationFailure(["bad input"]), StatusCodes.Status400BadRequest, "Validation failed" },
        { Result.Unauthorized("no token"), StatusCodes.Status401Unauthorized, "Unauthorized" },
        { Result.Forbidden("kill switch"), StatusCodes.Status403Forbidden, "Forbidden" },
        { Result.NotFound("no such thing"), StatusCodes.Status404NotFound, "Not found" },
        { Result.Conflict("already resolved"), StatusCodes.Status409Conflict, "Conflict" },
    };

    [Theory]
    [MemberData(nameof(ClassifiedFailures))]
    public void FailureResponse_ClassifiedFailure_MapsStatusTitleAndDetail(
        Result result, int expectedStatus, string expectedTitle)
    {
        var response = CreateController().FailureResponse(result, "Operation failed");

        var problem = Payload(response);
        problem.Status.Should().Be(expectedStatus);
        problem.Title.Should().Be(expectedTitle);
        problem.Detail.Should().Be(string.Join(" / ", result.Errors));
    }

    [Fact]
    public void FailureResponse_GeneralFailure_Returns500WithoutLeakingErrorText()
    {
        var response = CreateController()
            .FailureResponse(Result.Fail("SqliteException: no such table at C:\\secret\\path"), "Widget operation failed");

        var problem = Payload(response);
        problem.Status.Should().Be(StatusCodes.Status500InternalServerError);
        problem.Title.Should().Be("Widget operation failed");
        problem.Detail.Should().NotContain("SqliteException").And.NotContain("secret");
    }

    [Fact]
    public void FailureResponse_UnmappedClassifiedType_FallsBackToOpaque500()
    {
        var response = CreateController()
            .FailureResponse(Result.GovernanceBlocked("policy X vetoed the call"), "Operation failed");

        var problem = Payload(response);
        problem.Status.Should().Be(StatusCodes.Status500InternalServerError);
        problem.Detail.Should().NotContain("policy X");
    }
}
