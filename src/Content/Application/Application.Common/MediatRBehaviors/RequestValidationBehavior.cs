using Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Common.MediatRBehaviors;

/// <summary>
/// Runs all registered FluentValidation validators for the request in parallel.
/// When <c>TResponse</c> is a <see cref="Result{T}"/>, returns a validation failure result
/// instead of throwing — per project convention "never throw for expected failures."
/// For non-Result responses, throws <see cref="Exceptions.ExceptionTypes.ValidationException"/>
/// as a fallback.
/// </summary>
/// <remarks>
/// Pipeline position: 7 (after authorization and tool permissions — only validate
/// requests the caller is authorized to make).
/// </remarks>
public sealed class RequestValidationBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;
    private readonly ILogger<RequestValidationBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestValidationBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public RequestValidationBehavior(
        IEnumerable<IValidator<TRequest>> validators,
        ILogger<RequestValidationBehavior<TRequest, TResponse>> logger)
    {
        _validators = validators;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var validatorArray = _validators as IValidator<TRequest>[] ?? _validators.ToArray();
        if (validatorArray.Length == 0)
            return await next();

        var context = new ValidationContext<TRequest>(request);
        var results = await Task.WhenAll(
            validatorArray.Select(v => v.ValidateAsync(context, cancellationToken)));

        // Avoid LINQ allocations on happy path
        List<FluentValidation.Results.ValidationFailure>? failures = null;
        foreach (var r in results)
        {
            foreach (var error in r.Errors)
            {
                failures ??= [];
                failures.Add(error);
            }
        }

        if (failures is null || failures.Count == 0)
            return await next();

        _logger.LogDebug("Validation failed for {RequestName} with {ErrorCount} errors",
            typeof(TRequest).Name, failures.Count);

        var errorMessages = failures
            .Select(f => $"{f.PropertyName}: {f.ErrorMessage}")
            .ToList()
            .AsReadOnly();

        if (ResultHelper.TryCreateValidationFailure<TResponse>(errorMessages, out var failureResult))
            return failureResult;

        throw new Exceptions.ExceptionTypes.ValidationException(failures);
    }
}
