using Domain.AI.Prompts;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Prompts;

/// <summary>
/// Tests for <see cref="PromptCacheBreakReport"/> record — computed properties, construction.
/// </summary>
public sealed class PromptCacheBreakReportTests
{
    [Fact]
    public void HasChanges_NothingChanged_ReturnsFalse()
    {
        var report = CreateReport(systemChanged: false, toolsChanged: false);

        report.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void HasChanges_SystemChanged_ReturnsTrue()
    {
        var report = CreateReport(systemChanged: true, toolsChanged: false);

        report.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void HasChanges_ToolsChanged_ReturnsTrue()
    {
        var report = CreateReport(systemChanged: false, toolsChanged: true);

        report.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void HasChanges_BothChanged_ReturnsTrue()
    {
        var report = CreateReport(systemChanged: true, toolsChanged: true);

        report.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void AllProperties_SetExplicitly_RetainValues()
    {
        var previous = CreateSnapshot("sys-old", "tools-old");
        var current = CreateSnapshot("sys-new", "tools-new");

        var report = new PromptCacheBreakReport
        {
            SystemChanged = true,
            ToolsChanged = true,
            ChangedToolNames = ["bash", "file_system"],
            Previous = previous,
            Current = current
        };

        report.SystemChanged.Should().BeTrue();
        report.ToolsChanged.Should().BeTrue();
        report.ChangedToolNames.Should().HaveCount(2);
        report.Previous.Should().Be(previous);
        report.Current.Should().Be(current);
    }

    private static PromptCacheBreakReport CreateReport(bool systemChanged, bool toolsChanged) =>
        new()
        {
            SystemChanged = systemChanged,
            ToolsChanged = toolsChanged,
            ChangedToolNames = toolsChanged ? ["bash"] : [],
            Previous = CreateSnapshot("prev-sys", "prev-tools"),
            Current = CreateSnapshot("cur-sys", "cur-tools")
        };

    private static PromptHashSnapshot CreateSnapshot(string sysHash, string toolsHash) =>
        new()
        {
            SystemHash = sysHash,
            ToolsHash = toolsHash,
            PerToolHashes = new Dictionary<string, string>(),
            Timestamp = DateTimeOffset.UtcNow
        };
}
