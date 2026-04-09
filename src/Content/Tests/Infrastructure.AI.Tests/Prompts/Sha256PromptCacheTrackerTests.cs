using FluentAssertions;
using Infrastructure.AI.Prompts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Prompts;

/// <summary>
/// Tests for <see cref="Sha256PromptCacheTracker"/> snapshot creation and comparison.
/// </summary>
public sealed class Sha256PromptCacheTrackerTests
{
    private readonly Sha256PromptCacheTracker _sut = new(NullLogger<Sha256PromptCacheTracker>.Instance);

    private static AIFunction CreateTool(string name, string description = "A tool")
    {
        return AIFunctionFactory.Create(
            () => "result",
            new AIFunctionFactoryOptions { Name = name, Description = description });
    }

    [Fact]
    public void TakeSnapshot_SameInput_ProducesSameHashes()
    {
        var tools = new List<AITool> { CreateTool("read_file"), CreateTool("write_file") };

        var snapshot1 = _sut.TakeSnapshot("You are an agent.", tools);
        var snapshot2 = _sut.TakeSnapshot("You are an agent.", tools);

        snapshot1.SystemHash.Should().Be(snapshot2.SystemHash);
        snapshot1.ToolsHash.Should().Be(snapshot2.ToolsHash);
        snapshot1.PerToolHashes.Should().BeEquivalentTo(snapshot2.PerToolHashes);
    }

    [Fact]
    public void TakeSnapshot_DifferentPrompt_ProducesDifferentSystemHash()
    {
        var tools = new List<AITool> { CreateTool("read_file") };

        var snapshot1 = _sut.TakeSnapshot("You are an agent.", tools);
        var snapshot2 = _sut.TakeSnapshot("You are a different agent.", tools);

        snapshot1.SystemHash.Should().NotBe(snapshot2.SystemHash);
        snapshot1.ToolsHash.Should().Be(snapshot2.ToolsHash);
    }

    [Fact]
    public void TakeSnapshot_DifferentTools_ProducesDifferentToolsHash()
    {
        var tools1 = new List<AITool> { CreateTool("read_file") };
        var tools2 = new List<AITool> { CreateTool("write_file") };

        var snapshot1 = _sut.TakeSnapshot("You are an agent.", tools1);
        var snapshot2 = _sut.TakeSnapshot("You are an agent.", tools2);

        snapshot1.SystemHash.Should().Be(snapshot2.SystemHash);
        snapshot1.ToolsHash.Should().NotBe(snapshot2.ToolsHash);
    }

    [Fact]
    public void Compare_NoChanges_ReturnsNull()
    {
        var tools = new List<AITool> { CreateTool("read_file") };

        var snapshot1 = _sut.TakeSnapshot("You are an agent.", tools);
        var snapshot2 = _sut.TakeSnapshot("You are an agent.", tools);

        var report = _sut.Compare(snapshot1, snapshot2);

        report.Should().BeNull();
    }

    [Fact]
    public void Compare_SystemChanged_ReportsSystemChange()
    {
        var tools = new List<AITool> { CreateTool("read_file") };

        var previous = _sut.TakeSnapshot("You are an agent.", tools);
        var current = _sut.TakeSnapshot("You are a modified agent.", tools);

        var report = _sut.Compare(previous, current);

        report.Should().NotBeNull();
        report!.SystemChanged.Should().BeTrue();
        report.ToolsChanged.Should().BeFalse();
        report.ChangedToolNames.Should().BeEmpty();
        report.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void Compare_ToolChanged_ReportsSpecificTool()
    {
        var previous = _sut.TakeSnapshot("prompt", new List<AITool>
        {
            CreateTool("read_file", "Reads a file"),
            CreateTool("write_file", "Writes a file")
        });

        var current = _sut.TakeSnapshot("prompt", new List<AITool>
        {
            CreateTool("read_file", "Reads a file from disk"),
            CreateTool("write_file", "Writes a file")
        });

        var report = _sut.Compare(previous, current);

        report.Should().NotBeNull();
        report!.SystemChanged.Should().BeFalse();
        report.ToolsChanged.Should().BeTrue();
        report.ChangedToolNames.Should().ContainSingle().Which.Should().Be("read_file");
    }

    [Fact]
    public void Compare_ToolAdded_ReportsNewTool()
    {
        var previous = _sut.TakeSnapshot("prompt", new List<AITool>
        {
            CreateTool("read_file")
        });

        var current = _sut.TakeSnapshot("prompt", new List<AITool>
        {
            CreateTool("read_file"),
            CreateTool("write_file")
        });

        var report = _sut.Compare(previous, current);

        report.Should().NotBeNull();
        report!.ToolsChanged.Should().BeTrue();
        report.ChangedToolNames.Should().Contain("write_file");
        report.ChangedToolNames.Should().NotContain("read_file");
    }

    [Fact]
    public void Compare_ToolRemoved_ReportsRemovedTool()
    {
        var previous = _sut.TakeSnapshot("prompt", new List<AITool>
        {
            CreateTool("read_file"),
            CreateTool("write_file")
        });

        var current = _sut.TakeSnapshot("prompt", new List<AITool>
        {
            CreateTool("read_file")
        });

        var report = _sut.Compare(previous, current);

        report.Should().NotBeNull();
        report!.ToolsChanged.Should().BeTrue();
        report.ChangedToolNames.Should().Contain("write_file");
        report.ChangedToolNames.Should().NotContain("read_file");
    }
}
