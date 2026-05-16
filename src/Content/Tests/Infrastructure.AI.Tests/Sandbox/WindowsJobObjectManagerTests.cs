using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Domain.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Sandbox;
using Xunit;

namespace Infrastructure.AI.Tests.Sandbox;

[Trait("Category", "WindowsOnly")]
[SupportedOSPlatform("windows")]
public class WindowsJobObjectManagerTests
{
    [Fact]
    public void CreateAndAssign_SetsResourceLimits()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var manager = new WindowsJobObjectManager();
        var limits = new ResourceLimits
        {
            MemoryLimitBytes = 128 * 1024 * 1024,
            CpuTimeSeconds = 10,
            MaxSubprocesses = 2
        };

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 5 127.0.0.1",
            CreateNoWindow = true,
            UseShellExecute = false
        })!;

        try
        {
            manager.SetLimits(limits);
            var act = () => manager.AssignProcess(process);
            act.Should().NotThrow();
        }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
    }

    [Fact]
    public void Dispose_ClosesJobHandle()
    {
        if (!OperatingSystem.IsWindows()) return;

        var manager = new WindowsJobObjectManager();

        var act = () => manager.Dispose();

        act.Should().NotThrow();
        act.Should().NotThrow(); // double dispose safe
    }

    [Fact]
    public void MemoryLimit_QueryReturnsUsage()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var manager = new WindowsJobObjectManager();
        var limits = new ResourceLimits { MemoryLimitBytes = 50 * 1024 * 1024 };

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo test",
            CreateNoWindow = true,
            UseShellExecute = false
        })!;

        try
        {
            manager.SetLimits(limits);
            manager.AssignProcess(process);

            var usage = manager.QueryUsage();

            usage.Should().NotBeNull();
            usage.MemoryBytes.Should().BeGreaterThanOrEqualTo(0);
            usage.CpuTimeSeconds.Should().BeGreaterThanOrEqualTo(0);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
    }
}
