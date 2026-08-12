using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.AI.Escalation;
using FluentAssertions;
using Infrastructure.AI.Escalation;
using Xunit;

namespace Infrastructure.AI.Tests.Escalation;

/// <summary>
/// Tests for <see cref="ApproverDecisionJsonConverter"/> in isolation from either durable store —
/// the wire-compatibility rules a #321 durable-format migration depends on: a legacy boolean
/// verdict reads correctly, an unrecognized verdict fails closed rather than throwing, and the
/// converter round-trips every field of <see cref="ApproverDecision"/> under both naming
/// policies the two durable stores use.
/// </summary>
public sealed class ApproverDecisionJsonConverterTests
{
    private static JsonSerializerOptions SnakeCaseOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new ApproverDecisionJsonConverter() }
    };

    private static JsonSerializerOptions PascalCaseOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new ApproverDecisionJsonConverter() }
    };

    // ===== Legacy boolean shape (pre-#321) =====

    [Theory]
    [InlineData(true, ApproverVerdict.Approve)]
    [InlineData(false, ApproverVerdict.Deny)]
    public void Read_LegacyApprovedBooleanNoVerdictProperty_ResolvesCorrectVerdict(
        bool legacyApproved, ApproverVerdict expected)
    {
        var json = $$"""{"approver_name":"admin","approved":{{legacyApproved.ToString().ToLowerInvariant()}},"reason":"r","responded_at":"2026-01-01T00:00:00+00:00"}""";

        var decision = JsonSerializer.Deserialize<ApproverDecision>(json, SnakeCaseOptions());

        decision!.Verdict.Should().Be(expected);
    }

    [Fact]
    public void Read_NeitherVerdictNorApprovedPresent_FailsClosedToDeny()
    {
        // Fail-closed contract: an absent verdict must never default to Approve.
        var json = """{"approver_name":"admin","reason":"r","responded_at":"2026-01-01T00:00:00+00:00"}""";

        var decision = JsonSerializer.Deserialize<ApproverDecision>(json, SnakeCaseOptions());

        decision!.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Fact]
    public void Read_ModernVerdictProperty_TakesPrecedenceOverLegacyApproved()
    {
        // Mutation control: when both are present (should not happen in practice, since this
        // converter never writes the legacy mirror, but a hand-edited row could), Verdict wins.
        var json = """{"approver_name":"admin","verdict":"Revise","approved":false,"reason":"r","responded_at":"2026-01-01T00:00:00+00:00"}""";

        var decision = JsonSerializer.Deserialize<ApproverDecision>(json, SnakeCaseOptions());

        decision!.Verdict.Should().Be(ApproverVerdict.Revise);
    }

    // ===== Unknown verdict name: fail closed, never throw =====

    [Fact]
    public void Read_UnrecognizedVerdictName_FailsClosedToDeny_DoesNotThrow()
    {
        // A hand-edited row or a row written by a newer build could carry a verdict name this
        // build does not recognize. JsonStringEnumConverter throws on an unknown name for a
        // directly-typed enum property; this converter must not let that escape.
        var json = """{"approver_name":"admin","verdict":"Escalate","reason":"r","responded_at":"2026-01-01T00:00:00+00:00"}""";

        Action act = () => JsonSerializer.Deserialize<ApproverDecision>(json, SnakeCaseOptions());

        act.Should().NotThrow();
        var decision = JsonSerializer.Deserialize<ApproverDecision>(json, SnakeCaseOptions());
        decision!.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Theory]
    [InlineData("Deny,Approve")] // 0|1 = 1, indistinguishable from a clean "Approve" to Enum.IsDefined
    [InlineData("Deny,Revise")]  // 0|2 = 2, indistinguishable from a clean "Revise" to Enum.IsDefined
    [InlineData("Approve,Revise")] // 1|2 = 3, not a defined member either way
    public void Read_CommaSeparatedVerdict_FailsClosedToDeny_NotTheSmuggledMember(string smuggled)
    {
        // The regression a bare Enum.TryParse would reintroduce: it reads a comma-separated
        // string as a bitwise OR regardless of [Flags], and when the OR lands on a defined
        // member's numeric value, Enum.IsDefined cannot tell it apart from having named that
        // member directly. EnumNameHelper.TryParseName refuses any comma outright.
        var json = $$"""{"approver_name":"admin","verdict":"{{smuggled}}","reason":"r","responded_at":"2026-01-01T00:00:00+00:00"}""";

        var decision = JsonSerializer.Deserialize<ApproverDecision>(json, SnakeCaseOptions());

        decision!.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Theory]
    [InlineData("42")]   // out-of-range integer
    [InlineData(" 2")]   // numeric form behind a stray leading space
    public void Read_NumericVerdictForm_FailsClosedToDeny(string numeric)
    {
        var json = $$"""{"approver_name":"admin","verdict":"{{numeric}}","reason":"r","responded_at":"2026-01-01T00:00:00+00:00"}""";

        var decision = JsonSerializer.Deserialize<ApproverDecision>(json, SnakeCaseOptions());

        decision!.Verdict.Should().Be(ApproverVerdict.Deny);
    }

    [Fact]
    public void Read_RecognizedVerdictName_DoesNotFailClosed()
    {
        // Mutation control for the test above: a recognized name must resolve to itself, not
        // blanket-deny every payload regardless of content.
        var json = """{"approver_name":"admin","verdict":"Revise","reason":"r","responded_at":"2026-01-01T00:00:00+00:00"}""";

        var decision = JsonSerializer.Deserialize<ApproverDecision>(json, SnakeCaseOptions());

        decision!.Verdict.Should().Be(ApproverVerdict.Revise);
    }

    // ===== Write shape: no legacy mirror =====

    [Fact]
    public void Write_NeverEmitsLegacyApprovedProperty()
    {
        var decision = new ApproverDecision
        {
            ApproverName = "admin",
            Verdict = ApproverVerdict.Approve,
            RespondedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(decision, SnakeCaseOptions());

        // A single source of truth in a sealed governance payload: a mirror would let a
        // corrupted single field make two readers of the same record disagree.
        json.Should().NotContain("\"approved\"");
        json.Should().Contain("\"verdict\":\"Approve\"");
    }

    // ===== Full round-trip under both naming policies =====

    [Theory]
    [MemberData(nameof(BothOptionSets))]
    public void RoundTrip_AllFieldsPreserved_UnderBothNamingPolicies(JsonSerializerOptions options)
    {
        var original = new ApproverDecision
        {
            ApproverName = "admin",
            Verdict = ApproverVerdict.Revise,
            Reason = "operator note",
            Instructions = "use the other path",
            RespondedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero)
        };

        var json = JsonSerializer.Serialize(original, options);
        var roundTripped = JsonSerializer.Deserialize<ApproverDecision>(json, options);

        roundTripped.Should().BeEquivalentTo(original);
    }

    public static TheoryData<JsonSerializerOptions> BothOptionSets() => new()
    {
        SnakeCaseOptions(),
        PascalCaseOptions()
    };

    // ===== Converter completeness: every ApproverDecision property has a Wire counterpart =====

    [Fact]
    public void Wire_HasAPropertyForEveryApproverDecisionProperty()
    {
        // Guards against the "add a field to the domain type, forget the converter" trap: if a
        // future field is added to ApproverDecision without a matching Wire property, this fails
        // instead of the field silently vanishing on every serialize/deserialize.
        var wireType = typeof(ApproverDecisionJsonConverter).GetNestedType("Wire", BindingFlags.NonPublic);
        wireType.Should().NotBeNull("ApproverDecisionJsonConverter must declare a private Wire DTO");

        // CanWrite excludes get-only computed properties (e.g. IsApproved => Verdict == Approve),
        // which have no independent state and so never need a Wire counterpart — only real data
        // members (an init accessor counts as writable) can silently vanish on round-trip.
        var domainPropertyNames = typeof(ApproverDecision)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var wirePropertyNames = wireType!
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Wire additionally carries the legacy "Approved" fallback, which has no ApproverDecision
        // counterpart by design — every OTHER domain property must have a Wire counterpart.
        var missing = domainPropertyNames.Except(wirePropertyNames).ToList();
        missing.Should().BeEmpty("every ApproverDecision property must round-trip through Wire");
    }
}
