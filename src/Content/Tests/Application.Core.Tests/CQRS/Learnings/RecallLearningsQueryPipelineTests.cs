using Application.Common.MediatRBehaviors;
using Application.Core.CQRS.Learnings;
using Domain.Common;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Real-pipeline test for the HTTP-facing recall query: wires an actual MediatR pipeline with
/// <see cref="RequestValidationBehavior{TRequest,TResponse}"/> and the real validator, then
/// dispatches an invalid <see cref="RecallLearningsQuery"/>. This proves the wire-level bounds
/// short-circuit as a <c>Validation</c> Result failure in the pipeline itself — coverage the
/// controller tests (which mock <see cref="IMediator"/>) cannot provide.
/// </summary>
public sealed class RecallLearningsQueryPipelineTests
{
    private static ServiceProvider BuildPipeline()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Real MediatR over Application.Core (handlers resolve lazily, so unrelated handlers'
        // dependencies never need to be registered here) + the real validation behavior and
        // the real validator — the exact pieces that gate the HTTP recall surface.
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(RecallLearningsQueryHandler).Assembly));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RequestValidationBehavior<,>));
        services.AddTransient<IValidator<RecallLearningsQuery>, RecallLearningsQueryValidator>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Send_OverlongContext_ValidationFailureSurfacesThroughRealPipeline()
    {
        await using var provider = BuildPipeline();
        var mediator = provider.GetRequiredService<IMediator>();
        var query = new RecallLearningsQuery
        {
            Context = new string('x', LearningsValidationRules.MaxContextLength + 1)
        };

        var result = await mediator.Send(query);

        result.IsSuccess.Should().BeFalse(
            "an over-long context must be rejected by the validation behavior before any handler runs");
        result.FailureType.Should().Be(ResultFailureType.Validation,
            "the failure must map to HTTP 400 through the shared FailureResponse switch");
        result.Errors.Should().Contain(e =>
            e.Contains($"{LearningsValidationRules.MaxContextLength}"),
            "the error must name the enforced bound");
    }

    [Fact]
    public async Task Send_EmptyContext_ValidationFailureSurfacesThroughRealPipeline()
    {
        await using var provider = BuildPipeline();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new RecallLearningsQuery { Context = "" });

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Validation);
    }
}
