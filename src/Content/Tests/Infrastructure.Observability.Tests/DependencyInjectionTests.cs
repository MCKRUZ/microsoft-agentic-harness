using Application.Common.Interfaces.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.Observability.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddInfrastructureObservabilityDependencies_RegistersIOwnerOnlyDirectoryCreator_OnItsOwn()
    {
        // Found by code review on #738: Agent365StartupValidator takes IOwnerOnlyDirectoryCreator, but
        // the only registration for it lived in Infrastructure.AI's own DI module — a project this one
        // did not reference. Production only worked because the real composition root happens to wire
        // both modules together; nothing made that an enforced fact rather than a coincidence, which is
        // exactly the shape #671/#672/#673 already found and fixed for a different consumer of this same
        // seam (see Infrastructure.AI.Tests.DependencyInjectionTests). This test proves
        // AddInfrastructureObservabilityDependencies is now self-sufficient: called ALONE, with no other
        // module wired alongside it, IOwnerOnlyDirectoryCreator still resolves.
        var services = new ServiceCollection();

        services.AddInfrastructureObservabilityDependencies();
        using var provider = services.BuildServiceProvider();

        var creator = provider.GetService<IOwnerOnlyDirectoryCreator>();

        creator.Should().NotBeNull();
    }
}
