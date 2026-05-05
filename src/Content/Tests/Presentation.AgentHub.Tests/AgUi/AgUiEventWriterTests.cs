using System.Text;
using FluentAssertions;
using Presentation.AgentHub.AgUi;
using Xunit;

namespace Presentation.AgentHub.Tests.AgUi;

public class AgUiEventWriterTests
{
    [Fact]
    public async Task WriteAsync_FormatsAsSseDataFrame()
    {
        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);

        await writer.WriteAsync(new RunStartedEvent("t1", "r1"));

        var output = Encoding.UTF8.GetString(stream.ToArray());
        output.Should().StartWith("data: ");
        output.Should().EndWith("\n\n");
    }

    [Fact]
    public async Task WriteAsync_ProducesValidJson()
    {
        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);

        await writer.WriteAsync(new TextMessageContentEvent("m1", "hello"));

        var output = Encoding.UTF8.GetString(stream.ToArray());
        var jsonPart = output.Replace("data: ", "").TrimEnd('\n');

        var parsed = System.Text.Json.JsonDocument.Parse(jsonPart);
        parsed.RootElement.GetProperty("type").GetString().Should().Be("TEXT_MESSAGE_CONTENT");
        parsed.RootElement.GetProperty("messageId").GetString().Should().Be("m1");
        parsed.RootElement.GetProperty("delta").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task WriteAsync_MultipleEvents_WritesSequentialFrames()
    {
        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);

        await writer.WriteAsync(new RunStartedEvent("t1", "r1"));
        await writer.WriteAsync(new TextMessageStartEvent("m1", "assistant"));
        await writer.WriteAsync(new TextMessageContentEvent("m1", "Hi"));
        await writer.WriteAsync(new TextMessageEndEvent("m1"));
        await writer.WriteAsync(new RunFinishedEvent("t1", "r1"));

        var output = Encoding.UTF8.GetString(stream.ToArray());
        var frames = output.Split("data: ", StringSplitOptions.RemoveEmptyEntries);
        frames.Should().HaveCount(5);
    }

    [Fact]
    public async Task WriteAsync_NullOptionalFields_OmittedFromJson()
    {
        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);

        await writer.WriteAsync(new RunErrorEvent("fail"));

        var output = Encoding.UTF8.GetString(stream.ToArray());
        output.Should().NotContain("threadId");
        output.Should().NotContain("runId");
    }
}
