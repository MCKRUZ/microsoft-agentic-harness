using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Xunit;

namespace Infrastructure.Observability.Tests.Processors;

/// <summary>
/// Tests <see cref="LogRecordRedactionProcessor"/> by driving real
/// <see cref="LogRecord"/>s through the OpenTelemetry logging pipeline. The
/// <see cref="IContentRedactionFilter"/> is mocked (its own rule set is covered by
/// <c>DefaultContentRedactionFilter</c> tests) so these tests pin the processor's
/// own contract: which surfaces it scrubs, that it leaves non-string data alone,
/// and that it runs before a downstream exporter sees the record.
/// </summary>
public sealed class LogRecordRedactionProcessorTests
{
    private const string Marker = "SECRET";
    private const string Redacted = "[REDACTED]";

    /// <summary>A capturing "exporter" that snapshots each record synchronously in OnEnd
    /// (LogRecords are pooled and recycled, so fields are copied out immediately).</summary>
    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        public List<(string? Message, List<KeyValuePair<string, object?>> Attributes)> Records { get; } = [];

        public override void OnEnd(LogRecord data) =>
            Records.Add((data.FormattedMessage, data.Attributes?.ToList() ?? []));
    }

    private static Mock<IContentRedactionFilter> MockFilter()
    {
        // Stand-in for the real redactor: replace the marker token wherever it appears.
        var mock = new Mock<IContentRedactionFilter>();
        mock.Setup(f => f.Redact(It.IsAny<string?>(), It.IsAny<IReadOnlyList<RedactionCategory>>()))
            .Returns((string? s, IReadOnlyList<RedactionCategory> _) => s?.Replace(Marker, Redacted) ?? string.Empty);
        return mock;
    }

    private static LogsConfig EnabledConfig() => new()
    {
        OtelExportEnabled = true,
        RedactionEnabled = true,
        RedactionCategories = ["Email", "Generic"],
    };

    /// <summary>
    /// Runs a single log call through a pipeline with the redaction processor registered
    /// first and a capturing processor after it, returning what the capturer saw.
    /// </summary>
    private static CapturingProcessor RunPipeline(
        LogRecordRedactionProcessor redactor,
        Action<ILogger> log)
    {
        var capture = new CapturingProcessor();
        using (var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.AddProcessor(redactor);   // FIRST — must scrub before export
                options.AddProcessor(capture);     // AFTER — sees the redacted record
            });
        }))
        {
            log(loggerFactory.CreateLogger("test.redaction"));
        }

        return capture;
    }

    private static LogRecordRedactionProcessor CreateProcessor(LogsConfig config) =>
        new(MockFilter().Object, config, NullLogger<LogRecordRedactionProcessor>.Instance);

    [Fact]
    public void OnEnd_ScrubsFormattedMessage()
    {
        var capture = RunPipeline(
            CreateProcessor(EnabledConfig()),
            logger => logger.LogInformation("token is {Value}", Marker));

        capture.Records.Should().ContainSingle();
        capture.Records[0].Message.Should().Be($"token is {Redacted}");
    }

    [Fact]
    public void OnEnd_ScrubsStringAttributeValues()
    {
        var capture = RunPipeline(
            CreateProcessor(EnabledConfig()),
            logger => logger.LogInformation("value {Value}", Marker));

        // The structured field {Value} is promoted to an attribute; its value must be scrubbed.
        capture.Records[0].Attributes.Should()
            .Contain(kvp => kvp.Key == "Value" && (string?)kvp.Value == Redacted);
    }

    [Fact]
    public void OnEnd_LeavesNonStringAttributesUntouched()
    {
        var capture = RunPipeline(
            CreateProcessor(EnabledConfig()),
            logger => logger.LogInformation("count {Count}", 42));

        capture.Records[0].Attributes.Should()
            .Contain(kvp => kvp.Key == "Count" && (int)kvp.Value! == 42);
    }

    [Fact]
    public void OnEnd_RedactionDisabled_LeavesRecordUnchanged()
    {
        var config = EnabledConfig();
        config.RedactionEnabled = false;

        var capture = RunPipeline(
            CreateProcessor(config),
            logger => logger.LogInformation("token is {Value}", Marker));

        capture.Records[0].Message.Should().Be($"token is {Marker}");
        capture.Records[0].Attributes.Should()
            .Contain(kvp => kvp.Key == "Value" && (string?)kvp.Value == Marker);
    }

    [Fact]
    public void OnEnd_EnabledWithEmptyCategories_FallsBackToFullRedaction()
    {
        // Fail-safe (not fail-open): redaction requested but no category resolved — the
        // processor over-redacts with the full set rather than emitting unredacted content.
        var config = EnabledConfig();
        config.RedactionCategories = [];

        var capture = RunPipeline(
            CreateProcessor(config),
            logger => logger.LogInformation("token is {Value}", Marker));

        capture.Records[0].Message.Should().Be($"token is {Redacted}");
    }

    [Fact]
    public void OnEnd_EnabledWithAllInvalidCategories_FallsBackToFullRedaction()
    {
        var config = EnabledConfig();
        config.RedactionCategories = ["Bogus", "AlsoBogus"];

        var capture = RunPipeline(
            CreateProcessor(config),
            logger => logger.LogInformation("token is {Value}", Marker));

        capture.Records[0].Message.Should().Be($"token is {Redacted}");
    }

    [Fact]
    public void OnEnd_UnknownCategoryName_IsIgnoredAndKnownOnesStillApply()
    {
        var config = EnabledConfig();
        config.RedactionCategories = ["Email", "NotACategory"];

        var capture = RunPipeline(
            CreateProcessor(config),
            logger => logger.LogInformation("token is {Value}", Marker));

        // The unknown name is skipped at construction; the known category still redacts.
        capture.Records[0].Message.Should().Be($"token is {Redacted}");
    }

    [Fact]
    public void Constructor_NullFilter_Throws()
    {
        var act = () => new LogRecordRedactionProcessor(
            null!, EnabledConfig(), NullLogger<LogRecordRedactionProcessor>.Instance);

        act.Should().Throw<ArgumentNullException>();
    }
}
