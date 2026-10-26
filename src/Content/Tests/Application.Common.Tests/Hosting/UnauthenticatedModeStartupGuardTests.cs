using Application.Common.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Application.Common.Tests.Hosting;

/// <summary>
/// <see cref="UnauthenticatedModeStartupGuard"/> (#904): outside Development a host may serve without authentication
/// only when the deployment says so explicitly, and that choice is always logged.
/// </summary>
public sealed class UnauthenticatedModeStartupGuardTests
{
    private readonly Mock<ILogger<UnauthenticatedModeStartupGuard>> _logger = new();

    private UnauthenticatedModeStartupGuard Guard(string environment, bool overridden) => new(
        "Test anonymous mode (Test:AllowAnonymous=true)",
        "Test:AllowAnonymousOutsideDevelopment",
        overridden,
        Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environment),
        _logger.Object);

    private int Logged(LogLevel level) => _logger.Invocations.Count(i => (LogLevel)i.Arguments[0] == level);

    [Fact]
    public async Task StartAsync_Development_StartsAndWarns()
    {
        await Guard("Development", overridden: false).StartAsync(CancellationToken.None);

        Logged(LogLevel.Warning).Should().Be(1);
        Logged(LogLevel.Critical).Should().Be(0);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Container")]
    public async Task StartAsync_OutsideDevelopmentWithoutTheOverride_RefusesNamingTheOverride(string environment)
    {
        var act = () => Guard(environment, overridden: false).StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*'{environment}'*Refusing to start*Test:AllowAnonymousOutsideDevelopment=true*");
    }

    [Fact]
    public async Task StartAsync_OutsideDevelopmentWithTheOverride_StartsAndLogsCritical()
    {
        // The deliberate choice must be findable in any log review, on every start.
        await Guard("Production", overridden: true).StartAsync(CancellationToken.None);

        Logged(LogLevel.Critical).Should().Be(1);
    }
}
