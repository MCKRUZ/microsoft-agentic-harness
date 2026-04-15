using Application.Common;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Smoke tests that validate the project scaffold compiles and basic DI resolves.
/// These run as part of dotnet test — a failing build means these fail implicitly.
/// </summary>
public class ScaffoldTests
{
    [Fact]
    public void AgentHub_ProjectBuilds_WithoutErrors()
    {
        // This test passes if the assembly loads. Build failure prevents discovery.
        Assert.True(true);
    }

    [Fact]
    public void Presentation_Common_GetServices_Registers_IMediator()
    {
        // Arrange: build a minimal container using Application.Common dependencies
        var services = new ServiceCollection();
        services.AddApplicationCommonDependencies();
        var provider = services.BuildServiceProvider();

        // Act: resolve IMediator
        var mediator = provider.GetService<IMediator>();

        // Assert: MediatR is registered and resolves
        Assert.NotNull(mediator);
    }
}
