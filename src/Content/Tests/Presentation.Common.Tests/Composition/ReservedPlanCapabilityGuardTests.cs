using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;
using Domain.AI.Planner;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Startup;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Guards the composition-time refusal of a keyed <see cref="ITool"/> registered under a reserved
/// <see cref="PlanCapabilities"/> name.
/// </summary>
/// <remarks>
/// The collision is fail-open at runtime: plan capabilities and tool keys share one
/// <c>CapabilityEnvelope.AllowedTools</c> string space, so a tool keyed <c>llm_call</c> makes a
/// caller who was granted only the inference capability also able to invoke that tool. Nothing
/// downstream reports it — the run just succeeds with more authority than intended. These tests
/// pin the guard that turns it into a boot failure.
/// </remarks>
public sealed class ReservedPlanCapabilityGuardTests
{
    [Theory]
    [MemberData(nameof(ReservedNames))]
    public void Validate_ToolKeyedUnderAnyReservedName_Throws(string reservedName)
    {
        // Every reserved name must be covered, not just the two that exist today: a future
        // PlanCapabilities constant that the guard silently ignores is the same fail-open again.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool, StubTool>(reservedName);

        var act = () => services.ValidateNoReservedPlanCapabilityToolKeys();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{reservedName}*")
            .WithMessage($"*{typeof(StubTool).FullName}*");
    }

    [Fact]
    public void Validate_ToolKeyedUnderACaseVariantOfAReservedName_Throws()
    {
        // Keyed DI resolution is ordinal, but the envelope matches AllowedTools case-insensitively,
        // so "LLM_Call" is a real collision even though GetKeyedService("llm_call") would miss it.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool, StubTool>(PlanCapabilities.LlmCall.ToUpperInvariant());

        var act = () => services.ValidateNoReservedPlanCapabilityToolKeys();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_FactoryRegisteredToolUnderAReservedName_ThrowsWithoutResolvingIt()
    {
        // Every harness tool is factory-registered. The guard must catch those too, and must not
        // invoke the factory to do it — a throwing factory here proves the scan is descriptor-only.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>(
            PlanCapabilities.Retrieval,
            (_, _) => throw new InvalidOperationException("factory must never run"));

        var act = () => services.ValidateNoReservedPlanCapabilityToolKeys();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{PlanCapabilities.Retrieval}*")
            .Which.Message.Should().NotContain("factory must never run");
    }

    [Fact]
    public void Validate_NonReservedToolKeysAndOtherKeyedServices_DoesNotThrow()
    {
        // The guard is scoped to ITool: keyed registrations of other contracts (step executors are
        // keyed by StepType) and ordinary tool keys must pass untouched.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool, StubTool>("file_system");
        services.AddKeyedSingleton<ITool>("echo_calculate", (_, _) => new StubTool());
        services.AddKeyedSingleton<StubTool>(StepType.LlmCall);
        services.AddSingleton<StubTool>();

        var act = () => services.ValidateNoReservedPlanCapabilityToolKeys();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_EveryCollision_IsNamedInASingleFailure()
    {
        // One boot should report the complete list, matching StartupRegistrationSmokeCheck's
        // aggregate-then-throw behaviour rather than making an operator fix them one per restart.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool, StubTool>(PlanCapabilities.Retrieval);
        services.AddKeyedSingleton<ITool, StubTool>(PlanCapabilities.LlmCall);

        var act = () => services.ValidateNoReservedPlanCapabilityToolKeys();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{PlanCapabilities.Retrieval}*")
            .WithMessage($"*{PlanCapabilities.LlmCall}*");
    }

    /// <summary>Every reserved plan-capability name, as xUnit theory data.</summary>
    public static TheoryData<string> ReservedNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in PlanCapabilities.ReservedNames)
            data.Add(name);
        return data;
    }

    private sealed class StubTool : ITool
    {
        public string Name => "stub";
        public string Description => "stub";
        public IReadOnlyList<string> SupportedOperations { get; } = ["noop"];

        public Task<ToolResult> ExecuteAsync(
            string operation,
            IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
