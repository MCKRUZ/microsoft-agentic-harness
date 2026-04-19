using Application.AI.Common.Models.Context;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Models.Context;

/// <summary>
/// Tests for context model records: <see cref="LoadedContextFile"/>,
/// <see cref="TruncatedArtifactInfo"/>, <see cref="Tier1LoadedContext"/>,
/// <see cref="Tier2LoadedContext"/>, <see cref="Tier3AccessConfig"/>,
/// and <see cref="AssembledContext"/>. Covers record equality, with-expressions,
/// default values, and deconstruction.
/// </summary>
public class ContextModelsTests
{
    [Fact]
    public void LoadedContextFile_ConstructsWithRequiredParameters()
    {
        var file = new LoadedContextFile("README.md", "/docs/README.md", "content", 25);

        file.Name.Should().Be("README.md");
        file.Path.Should().Be("/docs/README.md");
        file.Content.Should().Be("content");
        file.TokenCount.Should().Be(25);
        file.IsTruncated.Should().BeFalse();
        file.OriginalTokenCount.Should().BeNull();
    }

    [Fact]
    public void LoadedContextFile_ConstructsWithOptionalParameters()
    {
        var file = new LoadedContextFile("README.md", "/docs/README.md", "content", 25, true, 100);

        file.IsTruncated.Should().BeTrue();
        file.OriginalTokenCount.Should().Be(100);
    }

    [Fact]
    public void LoadedContextFile_Equality_SameValues_AreEqual()
    {
        var a = new LoadedContextFile("f", "p", "c", 10);
        var b = new LoadedContextFile("f", "p", "c", 10);

        a.Should().Be(b);
    }

    [Fact]
    public void LoadedContextFile_WithExpression_CreatesModifiedCopy()
    {
        var original = new LoadedContextFile("f", "p", "c", 10);
        var modified = original with { TokenCount = 20 };

        modified.TokenCount.Should().Be(20);
        modified.Name.Should().Be(original.Name);
        original.TokenCount.Should().Be(10);
    }

    [Fact]
    public void TruncatedArtifactInfo_ConstructsCorrectly()
    {
        var info = new TruncatedArtifactInfo("file.md", 500, 200, TruncationReason.Truncated);

        info.Name.Should().Be("file.md");
        info.OriginalTokens.Should().Be(500);
        info.IncludedTokens.Should().Be(200);
        info.Reason.Should().Be(TruncationReason.Truncated);
    }

    [Fact]
    public void TruncatedArtifactInfo_Skipped_HasNullIncludedTokens()
    {
        var info = new TruncatedArtifactInfo("file.md", 500, null, TruncationReason.Skipped);

        info.IncludedTokens.Should().BeNull();
    }

    [Fact]
    public void TruncationReason_HasExpectedValues()
    {
        Enum.GetValues<TruncationReason>().Should().HaveCount(3);
        TruncationReason.Truncated.Should().BeDefined();
        TruncationReason.Skipped.Should().BeDefined();
        TruncationReason.SkippedLowPriority.Should().BeDefined();
    }

    [Fact]
    public void Tier1LoadedContext_ConstructsCorrectly()
    {
        var files = new List<LoadedContextFile>
        {
            new("f1", "p1", "c1", 100)
        };

        var tier1 = new Tier1LoadedContext(files, 100, 3000);

        tier1.Files.Should().HaveCount(1);
        tier1.TotalTokens.Should().Be(100);
        tier1.MaxTokens.Should().Be(3000);
    }

    [Fact]
    public void Tier2LoadedContext_ConstructsCorrectly()
    {
        var files = new List<LoadedContextFile>();
        var truncated = new List<TruncatedArtifactInfo>
        {
            new("skipped.md", 500, null, TruncationReason.Skipped)
        };

        var tier2 = new Tier2LoadedContext(files, 0, 8000, truncated);

        tier2.Files.Should().BeEmpty();
        tier2.TotalTokens.Should().Be(0);
        tier2.MaxTokens.Should().Be(8000);
        tier2.TruncatedFiles.Should().HaveCount(1);
    }

    [Fact]
    public void Tier3AccessConfig_ConstructsCorrectly()
    {
        var paths = new List<string> { "/docs", "/templates" };
        var tier3 = new Tier3AccessConfig(paths, "Use tool X to look up files.");

        tier3.AllowedLookupPaths.Should().HaveCount(2);
        tier3.FallbackPrompt.Should().Be("Use tool X to look up files.");
    }

    [Fact]
    public void Tier3AccessConfig_NullFallbackPrompt_IsAllowed()
    {
        var tier3 = new Tier3AccessConfig([], null);

        tier3.AllowedLookupPaths.Should().BeEmpty();
        tier3.FallbackPrompt.Should().BeNull();
    }

    [Fact]
    public void AssembledContext_ConstructsCorrectly()
    {
        var tier1 = new Tier1LoadedContext([], 0, 3000);
        var tier2 = new Tier2LoadedContext([], 0, 8000, []);
        var tier3 = new Tier3AccessConfig([], null);

        var assembled = new AssembledContext(tier1, tier2, tier3, 0, "");

        assembled.Tier1.Should().BeSameAs(tier1);
        assembled.Tier2.Should().BeSameAs(tier2);
        assembled.Tier3.Should().BeSameAs(tier3);
        assembled.TotalTokens.Should().Be(0);
        assembled.FormattedPromptSection.Should().BeEmpty();
    }

    [Fact]
    public void AssembledContext_Equality_SameStructure_AreEqual()
    {
        var tier1 = new Tier1LoadedContext([], 0, 3000);
        var tier2 = new Tier2LoadedContext([], 0, 8000, []);
        var tier3 = new Tier3AccessConfig([], null);

        var a = new AssembledContext(tier1, tier2, tier3, 0, "prompt");
        var b = new AssembledContext(tier1, tier2, tier3, 0, "prompt");

        a.Should().Be(b);
    }
}
