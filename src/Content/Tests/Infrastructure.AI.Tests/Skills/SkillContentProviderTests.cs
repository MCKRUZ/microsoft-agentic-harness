using FluentAssertions;
using Infrastructure.AI.Skills;
using Xunit;

namespace Infrastructure.AI.Tests.Skills;

/// <summary>
/// Tests for ISkillContentProvider implementations.
/// </summary>
public sealed class SkillContentProviderTests
{
    [Fact]
    public async Task CandidateSkillContentProvider_PathInSnapshot_ReturnsSnapshotContent()
    {
        var snapshot = new Dictionary<string, string>
        {
            ["skills/foo/SKILL.md"] = "# Foo content"
        };
        var provider = new CandidateSkillContentProvider(snapshot);

        var result = await provider.GetSkillContentAsync("skills/foo/SKILL.md");

        result.Should().Be("# Foo content");
    }

    [Fact]
    public async Task CandidateSkillContentProvider_PathNotInSnapshot_ReturnsNull()
    {
        var snapshot = new Dictionary<string, string>
        {
            ["skills/foo/SKILL.md"] = "# Foo content"
        };
        var provider = new CandidateSkillContentProvider(snapshot);

        var result = await provider.GetSkillContentAsync("skills/bar/SKILL.md");

        result.Should().BeNull();
    }

    [Fact]
    public async Task FileSystemSkillContentProvider_ExistingFile_ReturnsContent()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "# Skill content");
            var provider = new FileSystemSkillContentProvider();

            var result = await provider.GetSkillContentAsync(tempFile);

            result.Should().Be("# Skill content");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task FileSystemSkillContentProvider_NonExistentFile_ReturnsNull()
    {
        var provider = new FileSystemSkillContentProvider();
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md");

        var result = await provider.GetSkillContentAsync(nonExistentPath);

        result.Should().BeNull();
    }
}
