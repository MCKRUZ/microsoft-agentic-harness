using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.AI.Escalation;
using Xunit;

namespace Domain.AI.Tests.Escalation;

/// <summary>
/// Tests for <see cref="EscalationExecutionRecord"/>'s factories — the shape that makes a failure
/// with no reason, or a never-executed record with no reason, unconstructable rather than merely
/// discouraged.
/// </summary>
public sealed class EscalationExecutionRecordTests
{
    private static readonly Guid EscalationId = Guid.NewGuid();
    private static readonly DateTimeOffset ReportedAt = DateTimeOffset.UtcNow;
    private const string ReportedBy = "tool-invocation";

    [Fact]
    public void Succeeded_SetsExpectedShape()
    {
        var record = EscalationExecutionRecord.Succeeded(EscalationId, ReportedAt, ReportedBy);

        Assert.Equal(EscalationId, record.EscalationId);
        Assert.Equal(EscalationExecutionStatus.Succeeded, record.Status);
        Assert.Null(record.FailureReason);
        Assert.Null(record.NotExecutedReason);
        Assert.Equal(ReportedAt, record.ReportedAt);
        Assert.Equal(ReportedBy, record.ReportedBy);
    }

    [Fact]
    public void Failed_SetsExpectedShape()
    {
        var record = EscalationExecutionRecord.Failed(
            EscalationId, "permission denied", FailureTextSubstitution.None, ReportedAt, ReportedBy);

        Assert.Equal(EscalationExecutionStatus.Failed, record.Status);
        Assert.Equal("permission denied", record.FailureReason);
        Assert.Equal(FailureTextSubstitution.None, record.FailureReasonSubstitution);
        Assert.Null(record.NotExecutedReason);
    }

    [Fact]
    public void Failed_SubstitutedText_CarriesTheSubstitutionReason()
    {
        // #472: the whole point of the new parameter — a caller can tell a placeholder apart from
        // the tool's own text without relying on the string's exact wording.
        var record = EscalationExecutionRecord.Failed(
            EscalationId, "[tool failure text withheld: sanitization removed all content]",
            FailureTextSubstitution.SanitizedToEmpty, ReportedAt, ReportedBy);

        Assert.Equal(FailureTextSubstitution.SanitizedToEmpty, record.FailureReasonSubstitution);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Failed_BlankFailureReason_Throws(string? failureReason)
    {
        // The whole point of the private constructor: a Failed record with nothing an approver can
        // read is indistinguishable from success, so this shape must never exist rather than be
        // guarded against at every reader.
        Assert.ThrowsAny<ArgumentException>(
            () => EscalationExecutionRecord.Failed(
                EscalationId, failureReason!, FailureTextSubstitution.None, ReportedAt, ReportedBy));
    }

    [Fact]
    public void Failed_NonBlankFailureReason_MutationControl_DoesNotThrow()
    {
        var exception = Record.Exception(
            () => EscalationExecutionRecord.Failed(
                EscalationId, "boom", FailureTextSubstitution.None, ReportedAt, ReportedBy));

        Assert.Null(exception);
    }

    [Fact]
    public void NeverExecuted_SetsExpectedShape()
    {
        var record = EscalationExecutionRecord.NeverExecuted(
            EscalationId, EscalationNotExecutedReason.RunCancelled, ReportedAt, ReportedBy);

        Assert.Equal(EscalationExecutionStatus.NeverExecuted, record.Status);
        Assert.Null(record.FailureReason);
        Assert.Equal(EscalationNotExecutedReason.RunCancelled, record.NotExecutedReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Succeeded_BlankReportedBy_Throws(string? reportedBy)
    {
        // ReportedBy is what makes "nobody reported" distinguishable from "this site reported" when
        // auditing which raising sites implement execution reporting — a blank value defeats that.
        Assert.ThrowsAny<ArgumentException>(
            () => EscalationExecutionRecord.Succeeded(EscalationId, ReportedAt, reportedBy!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NeverExecuted_BlankReportedBy_Throws(string? reportedBy)
    {
        Assert.ThrowsAny<ArgumentException>(() => EscalationExecutionRecord.NeverExecuted(
            EscalationId, EscalationNotExecutedReason.RunCancelled, ReportedAt, reportedBy!));
    }

    // #472: the same snake_case + JsonStringEnumConverter shape JsonlEscalationAuditStore actually
    // uses to write and read this record — see its SerializeOptions/DeserializeOptions.
    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void JsonRoundTrip_NewShape_PreservesFailureReasonSubstitution()
    {
        var record = EscalationExecutionRecord.Failed(
            EscalationId, "permission denied", FailureTextSubstitution.SanitizedToEmpty, ReportedAt, ReportedBy);

        var json = JsonSerializer.Serialize(record, AuditJsonOptions);
        var rehydrated = JsonSerializer.Deserialize<EscalationExecutionRecord>(json, AuditJsonOptions);

        Assert.NotNull(rehydrated);
        Assert.Equal(FailureTextSubstitution.SanitizedToEmpty, rehydrated!.FailureReasonSubstitution);
    }

    [Fact]
    public void JsonRoundTrip_PreExistingAuditLine_MissingFailureReasonSubstitution_StillDeserializes()
    {
        // #472: EscalationExecutionRecord is JSON-serialized to an append-only, hash-chained audit
        // store — a line written before this field existed must still load. This is the actual
        // payload shape a pre-#472 line has: no failure_reason_substitution key at all, not a null
        // one (System.Text.Json's "missing property" and "property present but null" paths are
        // different code paths, and only the former is what an old file on disk actually contains).
        const string preExistingLine =
            """
            {"escalation_id":"11111111-1111-1111-1111-111111111111","status":"Failed",
            "failure_reason":"downstream API returned 500","not_executed_reason":null,
            "reported_at":"2026-01-01T00:00:00+00:00","reported_by":"agent-turn"}
            """;

        var record = JsonSerializer.Deserialize<EscalationExecutionRecord>(preExistingLine, AuditJsonOptions);

        Assert.NotNull(record);
        Assert.Equal("downstream API returned 500", record!.FailureReason);
        // The only backward-compatible reading of "absent" — see FailureReasonSubstitution's own doc.
        Assert.Null(record.FailureReasonSubstitution);
    }
}
