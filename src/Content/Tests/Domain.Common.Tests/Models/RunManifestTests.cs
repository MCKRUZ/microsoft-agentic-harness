using Domain.Common.Models;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Models;

/// <summary>
/// Tests for <see cref="RunManifest"/> record — required properties, defaults,
/// and record behavior.
/// </summary>
public class RunManifestTests
{
    [Fact]
    public void Construction_WithRequiredProperties_Succeeds()
    {
        var started = DateTimeOffset.UtcNow;
        var manifest = new RunManifest
        {
            RunId = "run-001",
            StartedAt = started
        };

        manifest.RunId.Should().Be("run-001");
        manifest.StartedAt.Should().Be(started);
    }

    [Fact]
    public void OptionalProperties_DefaultCorrectly()
    {
        var manifest = new RunManifest
        {
            RunId = "run-001",
            StartedAt = DateTimeOffset.UtcNow
        };

        manifest.Phase.Should().BeNull();
        manifest.CompletedAt.Should().BeNull();
        manifest.LogEntryCount.Should().Be(0);
        manifest.ActivityId.Should().BeNull();
    }

    [Fact]
    public void Construction_WithAllProperties_SetsCorrectly()
    {
        var started = DateTimeOffset.UtcNow;
        var completed = started.AddMinutes(5);
        var manifest = new RunManifest
        {
            RunId = "run-001",
            Phase = "execution",
            StartedAt = started,
            CompletedAt = completed,
            LogEntryCount = 150,
            ActivityId = "activity-xyz"
        };

        manifest.Phase.Should().Be("execution");
        manifest.CompletedAt.Should().Be(completed);
        manifest.LogEntryCount.Should().Be(150);
        manifest.ActivityId.Should().Be("activity-xyz");
    }

    [Fact]
    public void WithExpression_DoesNotMutateOriginal()
    {
        var original = new RunManifest
        {
            RunId = "run-001",
            StartedAt = DateTimeOffset.UtcNow,
            LogEntryCount = 10
        };

        var modified = original with { LogEntryCount = 20 };

        original.LogEntryCount.Should().Be(10);
        modified.LogEntryCount.Should().Be(20);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var ts = DateTimeOffset.UtcNow;
        var a = new RunManifest { RunId = "r1", StartedAt = ts };
        var b = new RunManifest { RunId = "r1", StartedAt = ts };

        a.Should().Be(b);
    }
}
