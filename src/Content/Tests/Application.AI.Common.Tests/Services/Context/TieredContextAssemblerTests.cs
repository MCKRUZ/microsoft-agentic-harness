using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Services.Context;
using Domain.AI.Skills;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Context;

/// <summary>
/// Tests for <see cref="TieredContextAssembler"/> covering all three tiers of progressive
/// disclosure, budget enforcement, truncation, file resolution across resource collections,
/// formatted prompt generation, and edge cases.
/// </summary>
public class TieredContextAssemblerTests
{
    private readonly Mock<IContextBudgetTracker> _budgetTracker;
    private readonly TieredContextAssembler _assembler;

    public TieredContextAssemblerTests()
    {
        _budgetTracker = new Mock<IContextBudgetTracker>();
        _assembler = new TieredContextAssembler(
            NullLogger<TieredContextAssembler>.Instance,
            _budgetTracker.Object);
    }

    [Fact]
    public async Task AssembleContext_NullContextLoading_ReturnsEmptyContext()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = null
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.TotalTokens.Should().Be(0);
        result.Tier1.Files.Should().BeEmpty();
        result.Tier2.Files.Should().BeEmpty();
        result.FormattedPromptSection.Should().BeEmpty();
    }

    [Fact]
    public async Task AssembleContext_NoConfiguration_ReturnsEmptyContext()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading()
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.TotalTokens.Should().Be(0);
    }

    [Fact]
    public async Task AssembleContext_Tier1WithLoadedFile_IncludesContent()
    {
        var skill = CreateSkillWithTier1File("org-context.md", "Organization overview content.");

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier1.Files.Should().HaveCount(1);
        result.Tier1.Files[0].Content.Should().Be("Organization overview content.");
        result.TotalTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AssembleContext_Tier1_RecordsAllocationOnBudgetTracker()
    {
        var skill = CreateSkillWithTier1File("org-context.md", "Content");

        await _assembler.AssembleContextAsync(skill);

        _budgetTracker.Verify(
            t => t.RecordAllocation("Test Skill", "tier1_context", It.IsAny<int>()),
            Times.Once);
    }

    [Fact]
    public async Task AssembleContext_Tier2WithLoadedFile_IncludesContent()
    {
        var skill = CreateSkillWithTier2File("domain-context.md", "Domain specific content.");

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier2.Files.Should().HaveCount(1);
        result.Tier2.Files[0].Content.Should().Be("Domain specific content.");
    }

    [Fact]
    public async Task AssembleContext_Tier2_RecordsAllocationOnBudgetTracker()
    {
        var skill = CreateSkillWithTier2File("domain-context.md", "Content");

        await _assembler.AssembleContextAsync(skill);

        _budgetTracker.Verify(
            t => t.RecordAllocation("Test Skill", "tier2_context", It.IsAny<int>()),
            Times.Once);
    }

    [Fact]
    public async Task AssembleContext_Tier3_ExposesLookupPaths()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier3 = new ContextTierConfig
                {
                    LookupPaths = ["inputs/raw/rfps/", "data/schemas/"],
                    FallbackPrompt = "Use file tools to access documents."
                }
            }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier3.AllowedLookupPaths.Should().HaveCount(2);
        result.Tier3.FallbackPrompt.Should().Be("Use file tools to access documents.");
    }

    [Fact]
    public async Task AssembleContext_Tier1ExceedsBudget_TruncatesContent()
    {
        var longContent = new string('x', 50000); // Way more than 3000 default token budget
        var skill = CreateSkillWithTier1File("big-file.md", longContent);

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier1.Files.Should().HaveCount(1);
        result.Tier1.Files[0].IsTruncated.Should().BeTrue();
        result.Tier1.Files[0].OriginalTokenCount.Should().NotBeNull();
        result.Tier1.TotalTokens.Should().BeLessThanOrEqualTo(result.Tier1.MaxTokens);
    }

    [Fact]
    public async Task AssembleContext_FileNotLoaded_SkipsFile()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig
                {
                    Files = ["missing-file.md"]
                }
            },
            Templates = { new SkillResource { FileName = "missing-file.md", Content = null } }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier1.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task AssembleContext_FormattedPrompt_ContainsTierHeaders()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig { Files = ["org.md"] },
                Tier2 = new ContextTierConfig { Files = ["domain.md"] },
                Tier3 = new ContextTierConfig
                {
                    LookupPaths = ["data/"],
                    FallbackPrompt = "Access via tools."
                }
            },
            Templates =
            {
                new SkillResource { FileName = "org.md", Content = "Org content" },
                new SkillResource { FileName = "domain.md", Content = "Domain content" }
            }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.FormattedPromptSection.Should().Contain("## Organizational Context");
        result.FormattedPromptSection.Should().Contain("## Domain Context");
        result.FormattedPromptSection.Should().Contain("## On-Demand Resources");
    }

    [Fact]
    public async Task AssembleContext_NullSkill_ThrowsArgumentNullException()
    {
        var act = async () => await _assembler.AssembleContextAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // --- Tier2 truncation ---

    [Fact]
    public async Task AssembleContext_Tier2ExceedsBudget_TruncatesContent()
    {
        var longContent = new string('y', 80000); // Way more than 8000 default token budget
        var skill = CreateSkillWithTier2File("big-domain.md", longContent);

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier2.Files.Should().HaveCount(1);
        result.Tier2.Files[0].IsTruncated.Should().BeTrue();
        result.Tier2.Files[0].OriginalTokenCount.Should().NotBeNull();
        result.Tier2.TotalTokens.Should().BeLessThanOrEqualTo(result.Tier2.MaxTokens);
        result.Tier2.TruncatedFiles.Should().HaveCount(1);
    }

    [Fact]
    public async Task AssembleContext_Tier2MultipleFiles_SecondSkippedWhenBudgetExhausted()
    {
        var largeContent = new string('z', 80000); // Fills entire budget
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier2 = new ContextTierConfig
                {
                    Files = ["file1.md", "file2.md"]
                }
            },
            Templates =
            {
                new SkillResource { FileName = "file1.md", Content = largeContent },
                new SkillResource { FileName = "file2.md", Content = "Small content" }
            }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        // First file truncated, second skipped entirely
        result.Tier2.TruncatedFiles.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    // --- Tier1 multiple files budget exhaustion ---

    [Fact]
    public async Task AssembleContext_Tier1MultipleFiles_StopsWhenBudgetExhausted()
    {
        var largeContent = new string('a', 50000); // Fills entire budget
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig
                {
                    Files = ["file1.md", "file2.md"]
                }
            },
            Templates =
            {
                new SkillResource { FileName = "file1.md", Content = largeContent },
                new SkillResource { FileName = "file2.md", Content = "Should be skipped" }
            }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        // Budget exhausted after truncating first file, second should be skipped
        result.Tier1.Files.Should().HaveCount(1);
    }

    // --- FindResource resolution across resource types ---

    [Fact]
    public async Task AssembleContext_FindsFileInReferences()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig { Files = ["ref-doc.md"] }
            },
            References = { new SkillResource { FileName = "ref-doc.md", Content = "Reference content" } }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier1.Files.Should().HaveCount(1);
        result.Tier1.Files[0].Content.Should().Be("Reference content");
    }

    [Fact]
    public async Task AssembleContext_FindsFileInAssets()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig { Files = ["schema.json"] }
            },
            Assets = { new SkillResource { FileName = "schema.json", Content = "{\"type\":\"object\"}" } }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier1.Files.Should().HaveCount(1);
        result.Tier1.Files[0].Content.Should().Contain("object");
    }

    [Fact]
    public async Task AssembleContext_FileNotFoundInAnyResourceType_SkipsFile()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig { Files = ["nonexistent.md"] }
            }
            // No templates, references, or assets
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier1.Files.Should().BeEmpty();
    }

    // --- FindResource by relative path ---

    [Fact]
    public async Task AssembleContext_FindsFileByRelativePath()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig { Files = ["templates/overview.md"] }
            },
            Templates =
            {
                new SkillResource
                {
                    FileName = "overview.md",
                    RelativePath = "templates/overview.md",
                    Content = "Overview from relative path"
                }
            }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier1.Files.Should().HaveCount(1);
        result.Tier1.Files[0].Content.Should().Be("Overview from relative path");
    }

    // --- Tier3 edge cases ---

    [Fact]
    public async Task AssembleContext_Tier3NoLookupPaths_ReturnsEmptyPaths()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier3 = new ContextTierConfig { FallbackPrompt = "Use tools." }
            }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier3.AllowedLookupPaths.Should().BeEmpty();
        result.Tier3.FallbackPrompt.Should().Be("Use tools.");
    }

    [Fact]
    public async Task AssembleContext_Tier3NullConfig_ReturnsEmptyTier3()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig { Files = ["org.md"] }
            },
            Templates = { new SkillResource { FileName = "org.md", Content = "Content" } }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier3.AllowedLookupPaths.Should().BeEmpty();
        result.Tier3.FallbackPrompt.Should().BeNull();
    }

    // --- Formatted prompt details ---

    [Fact]
    public async Task AssembleContext_FormattedPrompt_IncludesFileNames()
    {
        var skill = CreateSkillWithTier1File("strategy.md", "Strategy doc content.");

        var result = await _assembler.AssembleContextAsync(skill);

        result.FormattedPromptSection.Should().Contain("### strategy.md");
    }

    [Fact]
    public async Task AssembleContext_Tier3OnlyLookupPaths_IncludesPathsInPrompt()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier3 = new ContextTierConfig
                {
                    LookupPaths = ["data/specs/"]
                }
            }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.FormattedPromptSection.Should().Contain("data/specs/");
        result.FormattedPromptSection.Should().Contain("Available lookup paths");
    }

    // --- Custom max tokens ---

    [Fact]
    public async Task AssembleContext_CustomTier1MaxTokens_RespectsCustomBudget()
    {
        var content = new string('x', 400); // ~100 tokens
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig
                {
                    Files = ["small.md"],
                    MaxTokens = 50 // Very small budget
                }
            },
            Templates = { new SkillResource { FileName = "small.md", Content = content } }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier1.MaxTokens.Should().Be(50);
        result.Tier1.Files[0].IsTruncated.Should().BeTrue();
    }

    // --- Empty content file ---

    [Fact]
    public async Task AssembleContext_EmptyContentFile_SkipsFile()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig { Files = ["empty.md"] }
            },
            Templates = { new SkillResource { FileName = "empty.md", Content = "" } }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.Tier1.Files.Should().BeEmpty();
    }

    // --- Total tokens calculation ---

    [Fact]
    public async Task AssembleContext_TotalTokens_SumsTier1AndTier2()
    {
        var skill = new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig { Files = ["org.md"] },
                Tier2 = new ContextTierConfig { Files = ["domain.md"] }
            },
            Templates =
            {
                new SkillResource { FileName = "org.md", Content = "Org content here." },
                new SkillResource { FileName = "domain.md", Content = "Domain content here." }
            }
        };

        var result = await _assembler.AssembleContextAsync(skill);

        result.TotalTokens.Should().Be(result.Tier1.TotalTokens + result.Tier2.TotalTokens);
        result.TotalTokens.Should().BeGreaterThan(0);
    }

    private static SkillDefinition CreateSkillWithTier1File(string fileName, string content)
    {
        return new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier1 = new ContextTierConfig { Files = [fileName] }
            },
            Templates = { new SkillResource { FileName = fileName, Content = content } }
        };
    }

    private static SkillDefinition CreateSkillWithTier2File(string fileName, string content)
    {
        return new SkillDefinition
        {
            Id = "test-skill",
            Name = "Test Skill",
            ContextLoading = new ContextLoading
            {
                Tier2 = new ContextTierConfig { Files = [fileName] }
            },
            Templates = { new SkillResource { FileName = fileName, Content = content } }
        };
    }
}
