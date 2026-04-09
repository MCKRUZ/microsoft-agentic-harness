using Application.Common.MediatRBehaviors;
using Domain.Common;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using ValidationException = Application.Common.Exceptions.ExceptionTypes.ValidationException;

namespace Application.Common.Tests.MediatRBehaviors;

// Request types must be public so Moq can proxy IValidator<T> (FluentValidation is strong-named)
public record ValidationTestRequest : IRequest<Result<string>>;
public record ValidationNonResultRequest : IRequest<string>;

public class RequestValidationBehaviorTests
{
    private static RequestHandlerDelegate<T> NextReturning<T>(T value) =>
        () => Task.FromResult(value);

    [Fact]
    public async Task Handle_NoValidators_PassesThrough()
    {
        var behavior = new RequestValidationBehavior<ValidationTestRequest, Result<string>>(
            Enumerable.Empty<IValidator<ValidationTestRequest>>(),
            NullLogger<RequestValidationBehavior<ValidationTestRequest, Result<string>>>.Instance);
        var expected = Result<string>.Success("ok");

        var result = await behavior.Handle(
            new ValidationTestRequest(), NextReturning(expected), CancellationToken.None);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Handle_ValidRequest_PassesThrough()
    {
        var validator = new Mock<IValidator<ValidationTestRequest>>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<ValidationContext<ValidationTestRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var behavior = new RequestValidationBehavior<ValidationTestRequest, Result<string>>(
            new[] { validator.Object },
            NullLogger<RequestValidationBehavior<ValidationTestRequest, Result<string>>>.Instance);
        var expected = Result<string>.Success("ok");

        var result = await behavior.Handle(
            new ValidationTestRequest(), NextReturning(expected), CancellationToken.None);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Handle_InvalidRequest_ResultResponse_ReturnsValidationFailure()
    {
        var validator = new Mock<IValidator<ValidationTestRequest>>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<ValidationContext<ValidationTestRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(new[]
            {
                new ValidationFailure("Name", "Name is required")
            }));

        var behavior = new RequestValidationBehavior<ValidationTestRequest, Result<string>>(
            new[] { validator.Object },
            NullLogger<RequestValidationBehavior<ValidationTestRequest, Result<string>>>.Instance);

        var result = await behavior.Handle(
            new ValidationTestRequest(),
            NextReturning(Result<string>.Success("should not reach")),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Validation);
        result.Errors.Should().Contain(e => e.Contains("Name is required"));
    }

    [Fact]
    public async Task Handle_InvalidRequest_NonResultResponse_ThrowsValidationException()
    {
        var validator = new Mock<IValidator<ValidationNonResultRequest>>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<ValidationContext<ValidationNonResultRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(new[]
            {
                new ValidationFailure("Email", "Email is required")
            }));

        var behavior = new RequestValidationBehavior<ValidationNonResultRequest, string>(
            new[] { validator.Object },
            NullLogger<RequestValidationBehavior<ValidationNonResultRequest, string>>.Instance);

        var act = () => behavior.Handle(
            new ValidationNonResultRequest(),
            NextReturning("should not reach"),
            CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Handle_MultipleValidators_AggregatesErrors()
    {
        var validator1 = new Mock<IValidator<ValidationTestRequest>>();
        validator1.Setup(v => v.ValidateAsync(It.IsAny<ValidationContext<ValidationTestRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(new[]
            {
                new ValidationFailure("Name", "Name is required")
            }));

        var validator2 = new Mock<IValidator<ValidationTestRequest>>();
        validator2.Setup(v => v.ValidateAsync(It.IsAny<ValidationContext<ValidationTestRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(new[]
            {
                new ValidationFailure("Email", "Email is invalid")
            }));

        var behavior = new RequestValidationBehavior<ValidationTestRequest, Result<string>>(
            new[] { validator1.Object, validator2.Object },
            NullLogger<RequestValidationBehavior<ValidationTestRequest, Result<string>>>.Instance);

        var result = await behavior.Handle(
            new ValidationTestRequest(),
            NextReturning(Result<string>.Success("should not reach")),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
        result.Errors.Should().Contain(e => e.Contains("Name is required"));
        result.Errors.Should().Contain(e => e.Contains("Email is invalid"));
    }

    [Fact]
    public async Task Handle_ValidatorsAllPass_PassesThrough()
    {
        var validator1 = new Mock<IValidator<ValidationTestRequest>>();
        validator1.Setup(v => v.ValidateAsync(It.IsAny<ValidationContext<ValidationTestRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var validator2 = new Mock<IValidator<ValidationTestRequest>>();
        validator2.Setup(v => v.ValidateAsync(It.IsAny<ValidationContext<ValidationTestRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var behavior = new RequestValidationBehavior<ValidationTestRequest, Result<string>>(
            new[] { validator1.Object, validator2.Object },
            NullLogger<RequestValidationBehavior<ValidationTestRequest, Result<string>>>.Instance);
        var expected = Result<string>.Success("ok");

        var result = await behavior.Handle(
            new ValidationTestRequest(), NextReturning(expected), CancellationToken.None);

        result.Should().BeSameAs(expected);
    }
}
