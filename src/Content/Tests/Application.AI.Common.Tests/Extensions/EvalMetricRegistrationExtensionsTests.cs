using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.AI.Common.Extensions;
using Domain.AI.Evaluation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Application.AI.Common.Tests.Extensions;

/// <summary>
/// Guards the two invariants a correctness-review pass on #436 flagged as advisory but worth
/// covering: calling <see cref="EvalMetricRegistrationExtensions.AddEvalMetric{TMetric}"/> twice
/// must not crash a consumer that composes the eval framework more than once, and a keyed alias
/// that drifts from the metric's own <see cref="IEvalMetric.Key"/> must fail loudly rather than
/// silently registering under a string no <c>MetricSpec</c> will ever reference.
/// </summary>
public sealed class EvalMetricRegistrationExtensionsTests
{
    [Fact]
    public void AddEvalMetric_CalledTwiceForTheSameMetric_DoesNotThrow()
    {
        var services = new ServiceCollection();

        services.AddEvalMetric<FakeMetric>(FakeMetric.RegisteredKey);
        var act = () => services.AddEvalMetric<FakeMetric>(FakeMetric.RegisteredKey);

        act.Should().NotThrow(
            "a consumer that calls AddEvaluationDependencies (or AddApplicationAIDependencies) more " +
            "than once must not end up with a duplicate key that crashes EvalRunner's ToDictionary");

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IEvalMetric>().Should().ContainSingle(m => m.Key == FakeMetric.RegisteredKey,
            "the second call must be a no-op, not a second registration");
    }

    [Fact]
    public void AddEvalMetric_KeyedAliasMatchesMetricsOwnKey_ResolvesWithoutError()
    {
        var services = new ServiceCollection();
        services.AddEvalMetric<FakeMetric>(FakeMetric.RegisteredKey);
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredKeyedService<IEvalMetric>(FakeMetric.RegisteredKey);

        resolved.Key.Should().Be(FakeMetric.RegisteredKey);
    }

    [Fact]
    public void AddEvalMetric_KeyedAliasDoesNotMatchMetricsOwnKey_ThrowsOnResolution()
    {
        var services = new ServiceCollection();
        services.AddEvalMetric<FakeMetric>("mismatched.key");
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredKeyedService<IEvalMetric>("mismatched.key");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*mismatched.key*{FakeMetric.RegisteredKey}*",
                "a drifted keyed alias must fail loudly at resolution, not silently register under a " +
                "string no MetricSpec will ever reference");
    }

    private sealed class FakeMetric : IEvalMetric
    {
        public const string RegisteredKey = "fake.metric";

        public string Key => RegisteredKey;

        public Task<MetricScore> ScoreAsync(
            EvalCase @case, AgentInvocationResult output, MetricSpec spec, CancellationToken cancellationToken) =>
            throw new NotImplementedException("Never invoked — these tests only exercise DI registration.");
    }
}
