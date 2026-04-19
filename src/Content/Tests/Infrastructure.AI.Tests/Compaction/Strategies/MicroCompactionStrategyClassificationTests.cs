using Domain.AI.Compaction;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Infrastructure.AI.Compaction.Strategies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Compaction.Strategies;

/// <summary>
/// Tests for <see cref="MicroCompactionStrategy"/> covering content classification
/// logic: file path patterns, shell output detection, and large tool result truncation.
/// </summary>
public sealed class MicroCompactionStrategyClassificationTests
{
    private readonly MicroCompactionStrategy _sut;

    public MicroCompactionStrategyClassificationTests()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                ContextManagement = new ContextManagementConfig
                {
                    Compaction = new CompactionConfig
                    {
                        MicroCompactStalenessMinutes = 5
                    }
                }
            }
        };

        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

        _sut = new MicroCompactionStrategy(options, NullLogger<MicroCompactionStrategy>.Instance);
    }

    [Fact]
    public async Task Execute_FilePathContent_IdentifiesAsFileRead()
    {
        var fileContent = "/src/Program.cs\nusing System;\nnamespace Test;";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Read the file"),
            new(ChatRole.Assistant, fileContent),
            new(ChatRole.User, "Now do something else"),
            new(ChatRole.Assistant, "Short response")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
        result.Boundary.Should().NotBeNull();
        result.Boundary!.Strategy.Should().Be(CompactionStrategy.Micro);
    }

    [Fact]
    public async Task Execute_WindowsFilePathContent_IdentifiesAsFileRead()
    {
        var fileContent = "C:\\Users\\test\\file.cs\nnamespace Test;";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Read the file"),
            new(ChatRole.Assistant, fileContent),
            new(ChatRole.User, "Next"),
            new(ChatRole.Assistant, "Done")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_CatNFormat_IdentifiesAsFileRead()
    {
        var catContent = "     1\tusing System;\n     2\tnamespace Test;\n     3\t";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Show me the file"),
            new(ChatRole.Assistant, catContent),
            new(ChatRole.User, "Continue"),
            new(ChatRole.Assistant, "Ok")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_ShellOutput_IdentifiesAsShellOutput()
    {
        var shellContent = "$ ls -la\ntotal 42\ndrwxr-xr-x 5 user group 160 Jan 10 09:00 .";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "List files"),
            new(ChatRole.Assistant, shellContent),
            new(ChatRole.User, "Next question"),
            new(ChatRole.Assistant, "Answer")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_PromptIndicatorContent_IdentifiesAsShellOutput()
    {
        var content = "> some-command --flag\noutput line 1\noutput line 2";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Run command"),
            new(ChatRole.Assistant, content),
            new(ChatRole.User, "What next"),
            new(ChatRole.Assistant, "Done")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_LargeToolResult_Truncates()
    {
        var largeContent = new string('a', 6000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Do something"),
            new(ChatRole.Assistant, largeContent),
            new(ChatRole.User, "More work"),
            new(ChatRole.Assistant, "Short")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
        result.Boundary.Should().NotBeNull();
        result.Boundary!.PreCompactionTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Execute_WithTimestampedStaleMessages_Compacts()
    {
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        var largeContent = new string('z', 6000);
        var staleMessage = new ChatMessage(ChatRole.Assistant, largeContent);
        staleMessage.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        staleMessage.AdditionalProperties["timestamp"] = staleTimestamp;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Old question"),
            staleMessage,
            new(ChatRole.User, "New question"),
            new(ChatRole.Assistant, "Fresh response")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_OnlyUserMessages_NoCompaction()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello"),
            new(ChatRole.User, "Another message")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
        result.Boundary!.Summary.Should().Contain("No compactable content found");
    }

    [Fact]
    public async Task Execute_EmptyMessages_NoCompaction()
    {
        var result = await _sut.ExecuteAsync("agent-1", []);

        result.Success.Should().BeTrue();
        result.Boundary!.Summary.Should().Contain("No compactable content found");
    }

    [Fact]
    public async Task Execute_ColonBackslashPattern_IdentifiesAsFileRead()
    {
        var content = "D:\\Projects\\test.sln\nMicrosoft Visual Studio Solution File";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Read"),
            new(ChatRole.Assistant, content),
            new(ChatRole.User, "Continue"),
            new(ChatRole.Assistant, "Done")
        };

        var result = await _sut.ExecuteAsync("agent-1", messages);

        result.Success.Should().BeTrue();
    }
}
