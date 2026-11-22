using System.Text.Json;
using Application.AI.Common.Models.Conversations;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using Infrastructure.AI.Persistence.Entities;
using Xunit;

namespace Infrastructure.AI.Tests.Conversations;

/// <summary>
/// A widget saved before widgets carried a version reads back as <see cref="WidgetSpec.LegacyVersion"/>
/// (#342), on both stores' read paths.
/// </summary>
/// <remarks>
/// Every widget in an existing transcript has no version field. Reading one as "current" instead would
/// recreate the bug versioning exists to prevent: the first time a widget's argument shape moves to
/// version 2, every old copy would be read as version 2, fail to validate, and drop out of history.
/// </remarks>
public sealed class WidgetSpecVersionPersistenceTests
{
    private const string LegacyWidgetJson = """{"type":"render_table","args":{"columns":["Name"],"rows":[["Ada"]]}}""";

    [Fact]
    public void FileStoreSerializer_WidgetWithNoVersionField_ReadsAsLegacyVersion()
    {
        var widget = JsonSerializer.Deserialize<WidgetSpec>(LegacyWidgetJson, ConversationJson.Options);

        widget!.Version.Should().Be(WidgetSpec.LegacyVersion);
    }

    [Fact]
    public void DatabaseStoreMapper_WidgetRowWithNoVersionField_ReadsAsLegacyVersion()
    {
        var entity = new ConversationMessageEntity
        {
            ConversationId = "c-1",
            MessageId = Guid.NewGuid(),
            Role = MessageRole.Assistant,
            Content = string.Empty,
            Timestamp = DateTimeOffset.UtcNow,
            WidgetJson = LegacyWidgetJson,
        };

        var message = ConversationEntityMapper.ToMessage(entity);

        message.Widget!.Version.Should().Be(WidgetSpec.LegacyVersion);
    }

    [Fact]
    public void LegacyVersion_IsOne_TheVersionEveryWidgetWasAtWhenVersioningWasIntroduced()
    {
        // Pinned: changing this constant would re-label every pre-versioning widget already on disk.
        WidgetSpec.LegacyVersion.Should().Be(1);
    }

    [Fact]
    public void FileStoreSerializer_WritesTheVersion()
    {
        var widget = new WidgetSpec("render_table", JsonDocument.Parse("{}").RootElement, Version: 4);

        var json = JsonSerializer.Serialize(widget, ConversationJson.Options);

        json.Should().Contain("\"version\":4");
    }
}
