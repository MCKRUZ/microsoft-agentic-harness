using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Services.Context;
using Domain.AI.Skills;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Context;

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
