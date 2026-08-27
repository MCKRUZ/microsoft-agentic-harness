using Application.Core;
using Domain.AI.Planner;
using FluentAssertions;
using FluentValidation;
using Infrastructure.AI.Planner;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// Resolves every step-config validator <see cref="PlanValidator" /> depends on from a real,
/// production-shaped container — not a mock — so an assembly-scan break fails this test instead of
/// degrading <c>PlanValidator.ValidateConfig</c> to its fail-closed fallback silently in a real host.
/// </summary>
/// <remarks>
/// <para>
/// This is option 3 from #526. All six step-config validators reach the container through one
/// mechanism, <c>AddValidatorsFromAssembly</c> on the <c>Application.Core</c> assembly — there is no
/// per-type registration to verify individually, only whether the scan itself still reaches them. An
/// assembly split, a trimmed publish, or a refactor that moves the
/// <c>Application.Core/Validation/Planner</c> folder out of the scanned assembly would silently break
/// every one of them at once; this test is what turns that into a loud CI failure instead of a
/// warning log nobody reads until a plan they expected to be checked was not.
/// </para>
/// <para>
/// <c>PlanValidatorTests</c> exercises <c>PlanValidator.ValidateConfig&lt;T&gt;</c>'s behavior
/// against a <em>mocked</em> <c>IServiceProvider</c> that hands back canned validators — it proves the
/// method's logic, not that a real container actually resolves anything. This test proves the other
/// half: that <c>services.AddApplicationCoreDependencies()</c>, run exactly as the composition root
/// runs it, really does make every <c>IValidator&lt;T&gt;</c> for every known
/// <see cref="StepConfiguration" /> subtype resolvable.
/// </para>
/// </remarks>
public sealed class PlannerStepConfigValidatorWiringTests
{
    [Fact]
    public void RealContainer_ResolvesAValidatorForEveryKnownStepConfigurationType()
    {
        var services = new ServiceCollection();
        services.AddApplicationCoreDependencies();
        var provider = services.BuildServiceProvider();

        provider.GetService<IValidator<LlmCallConfig>>().Should().NotBeNull(
            "PlanValidator dispatches LlmCallConfig to ValidateConfig<T>, which fails the whole " +
            "plan closed if this cannot be resolved (#526)");
        provider.GetService<IValidator<ToolUseConfig>>().Should().NotBeNull();
        provider.GetService<IValidator<HumanGateConfig>>().Should().NotBeNull();
        provider.GetService<IValidator<ConditionalBranchConfig>>().Should().NotBeNull();
        provider.GetService<IValidator<SubPlanConfig>>().Should().NotBeNull();
        provider.GetService<IValidator<RetrievalStepConfiguration>>().Should().NotBeNull(
            "added alongside this test (#526) — until now RetrievalStepConfiguration had no switch " +
            "arm in PlanValidator and no validator anywhere, so every retrieval-step plan passed " +
            "unchecked regardless of what this test would have found");
    }
}
