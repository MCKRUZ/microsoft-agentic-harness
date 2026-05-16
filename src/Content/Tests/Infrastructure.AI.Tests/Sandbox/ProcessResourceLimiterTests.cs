using System.Diagnostics;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Sandbox;

[Trait("Category", "WindowsOnly")]
public class ProcessResourceLimiterTests
{
    [Fact]
    public void IProcessResourceLimiter_Interface_CanBeMocked()
    {
        var mock = new Mock<IProcessResourceLimiter>();
        mock.Setup(x => x.IsSupported).Returns(true);
        mock.Setup(x => x.Apply(It.IsAny<Process>(), It.IsAny<ResourceLimits>())).Returns(true);
        mock.Setup(x => x.GetUsage()).Returns(new ResourceUsage
        {
            MemoryBytes = 1024,
            CpuTimeSeconds = 0.5
        });

        mock.Object.IsSupported.Should().BeTrue();
        mock.Object.Apply(null!, new ResourceLimits()).Should().BeTrue();
        mock.Object.GetUsage().Should().NotBeNull();
        mock.Object.GetUsage()!.MemoryBytes.Should().Be(1024);
    }

    [Fact]
    public void NoOpProcessResourceLimiter_Apply_ReturnsFalseAndLogsWarning()
    {
        var logger = new Mock<ILogger<NoOpProcessResourceLimiter>>();
        using var limiter = new NoOpProcessResourceLimiter(logger.Object);

        limiter.IsSupported.Should().BeFalse();

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo test",
            CreateNoWindow = true,
            UseShellExecute = false
        })!;

        try
        {
            var applied = limiter.Apply(process, new ResourceLimits());

            applied.Should().BeFalse();
            limiter.GetUsage().Should().BeNull();

            logger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => true),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
        finally
        {
            process.WaitForExit(5000);
        }
    }
}
