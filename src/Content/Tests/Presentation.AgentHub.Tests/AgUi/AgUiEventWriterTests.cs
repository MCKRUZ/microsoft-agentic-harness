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
    public async Task WriteAsync_ConcurrentWrites_ProduceIntactNonInterleavedFrames()
    {
        // The agent runs tool calls concurrently (AllowConcurrentInvocation = true), and blocking-proxy
        // tools emit frames onto the same writer from those concurrent invocations. WriteAsync must
        // serialize so each frame is written atomically; without the gate, concurrent writes to the
        // underlying stream interleave bytes and corrupt the SSE.
        using var stream = new MemoryStream();
        var writer = new AgUiEventWriter(stream);

        const int count = 64;
        var writes = Enumerable.Range(0, count)
            .Select(i => writer.WriteAsync(new TextMessageContentEvent($"m{i}", $"delta-{i}")));
        await Task.WhenAll(writes);

        var frames = Encoding.UTF8.GetString(stream.ToArray())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        frames.Should().HaveCount(count);

        // Every frame must be a complete, parseable JSON object — proof that no two writes interleaved.
        var ids = frames
            .Select(f => System.Text.Json.JsonDocument.Parse(f.Replace("data: ", "")))
            .Select(d => d.RootElement.GetProperty("messageId").GetString())
            .ToList();
        ids.Should().BeEquivalentTo(Enumerable.Range(0, count).Select(i => $"m{i}"));
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

    [Fact]
    public async Task WriteAsync_RunErrorWithoutACode_LeavesTheCodeKeyOut()
    {
        // The AG-UI client schema types `code` as an optional string: absent parses, null does not, so a null written
        // here would make every ordinary run error unreadable to the client (#1036).
        var root = await WriteAndParse(new RunErrorEvent("fail"));

        root.TryGetProperty("code", out _).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_RunErrorWithACode_WritesIt()
    {
        var root = await WriteAndParse(new RunErrorEvent("blocked", AgUiErrorCodes.MessageRemoved));

        root.GetProperty("code").GetString().Should().Be(AgUiErrorCodes.MessageRemoved);
    }

    private static async Task<System.Text.Json.JsonElement> WriteAndParse(AgUiEvent evt)
    {
        using var stream = new MemoryStream();
        await new AgUiEventWriter(stream).WriteAsync(evt);
        var json = Encoding.UTF8.GetString(stream.ToArray()).Replace("data: ", "").TrimEnd('\n');
        return System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
    }
}
