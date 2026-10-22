using FluentAssertions;
using Infrastructure.AI.Helpers;
using Xunit;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// The shared deletion rules every file-store retention sweep uses (#901), checked here directly for the cases a store's
/// own tests cannot stage.
/// </summary>
public sealed class RetentionDirectoriesTests : IDisposable
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-120);
    private static readonly DateTime Cutoff = DateTime.UtcNow.AddDays(-90);

    private readonly string _unit = Path.Combine(Path.GetTempPath(), $"retention-unit-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_unit))
                Directory.Delete(_unit, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void AWriteThatLandsWhileTheSweepIsDeleting_KeepsTheUnit()
    {
        // The sweep judged the unit old, then the store wrote to it again (a workflow resumed and saved) before the
        // marker went. Deleting the marker now would destroy what that write just saved.
        Directory.CreateDirectory(_unit);
        var marker = Path.Combine(_unit, "state.json");
        File.WriteAllText(marker, "{}");
        File.WriteAllText(Path.Combine(_unit, "rendering.md"), "old");
        foreach (var file in Directory.EnumerateFiles(_unit))
            File.SetLastWriteTimeUtc(file, Old);

        var deleted = RetentionDirectories.DeleteOwnFiles(_unit, "state.json", relative =>
        {
            File.WriteAllText(marker, "{\"saved\":\"just now\"}");
            return relative == "rendering.md";
        }, Cutoff);

        deleted.Should().BeFalse();
        File.ReadAllText(marker).Should().Contain("just now");
    }

    [Fact]
    public void AnOldUnit_LosesItsOwnFilesAndMarker_AndIsRemovedWhenEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_unit, "sub"));
        File.WriteAllText(Path.Combine(_unit, "state.json"), "{}");
        File.WriteAllText(Path.Combine(_unit, "sub", "own.txt"), "x");
        foreach (var file in Directory.EnumerateFiles(_unit, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, Old);

        RetentionDirectories.DeleteOwnFiles(_unit, "state.json", relative => relative == "sub/own.txt", Cutoff)
            .Should().BeTrue();

        Directory.Exists(_unit).Should().BeFalse();
    }
}
