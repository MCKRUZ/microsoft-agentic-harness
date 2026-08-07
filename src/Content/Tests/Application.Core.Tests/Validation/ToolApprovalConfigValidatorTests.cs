using Application.AI.Common.Interfaces.Escalation;
using Application.Core.Validation;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="ToolApprovalConfigValidator"/>.
/// Pattern: CreateValidConfig() baseline, mutate one field per test.
/// </summary>
/// <remarks>
/// Every setting this validator guards ends up inside an <c>EscalationRequest</c> whose invariants
/// are enforced by throwing, which the approval router's fail-closed catch turns into a block. So the
/// cost of a bad value here is not a bad approval — it is <em>every</em> approval-required tool call
/// refused for the life of the process, diagnosable only from a per-call error log. These tests exist
/// to keep that failure at boot, where it names the setting.
/// </remarks>
public class ToolApprovalConfigValidatorTests
{
    private readonly ToolApprovalConfigValidator _validator = new();

    [Fact]
    public async Task Validate_ValidConfig_NoErrors()
    {
        var result = await _validator.ValidateAsync(CreateValidConfig());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_DefaultConfig_NoErrors()
    {
        // The shipped default — nothing set at all — must pass. A validator that rejects its own
        // default breaks every host that never opted in, which is all of them by default. This is
        // deliberately constructed with `new()` rather than from the baseline helper: a default is
        // untested unless a test builds the config with nothing configured.
        var result = await _validator.ValidateAsync(new ToolApprovalConfig());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_DisabledWithEmptyRoster_NoErrors()
    {
        // Rules are gated on Enabled: a host that has not opted in must not be forced to populate
        // settings it never reads.
        var config = CreateValidConfig();
        config = new ToolApprovalConfig
        {
            Enabled = false,
            Approvers = [],
            CriticalAtBlastRadius = config.CriticalAtBlastRadius
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_EnabledWithEmptyRoster_Fails()
    {
        // An escalation nobody can answer stalls the turn until it times out and then blocks anyway.
        var config = new ToolApprovalConfig { Enabled = true, Approvers = [] };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ToolApprovalConfig.Approvers));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validate_EnabledWithBlankApproverEntry_Fails(string blank)
    {
        // EscalationRequestInvariants rejects an empty approver name, so a single stray whitespace
        // entry alongside a real approver would refuse every approval-required call forever.
        var config = new ToolApprovalConfig { Enabled = true, Approvers = ["alice", blank] };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ToolApprovalConfig.Approvers));
    }

    [Theory]
    [InlineData("Trivial")]
    [InlineData("Low")]
    [InlineData("Medium")]
    [InlineData("High")]
    [InlineData("Critical")]
    [InlineData("critical")]
    public async Task Validate_DefinedBlastRadiusName_NoErrors(string radius)
    {
        var config = CreateValidConfig();
        config = Rebuild(config, radius);

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Catastrophic")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("99")]
    public async Task Validate_BlastRadiusThatIsNotAMemberName_Fails(string radius)
    {
        // Numeric forms are rejected on purpose: the shared parser accepts member NAMES only, so
        // "3" must not silently become High. Unvalidated, a bad value here parses to Critical and
        // merely narrows who is paged — a typo in a governance setting should never be discoverable
        // only by noticing that nobody was notified.
        var config = Rebuild(CreateValidConfig(), radius);

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(ToolApprovalConfig.CriticalAtBlastRadius));
    }

    [Fact]
    public async Task Validate_NullTimeout_NoErrors()
    {
        // Null is the documented "inherit EscalationConfig.DefaultTimeoutSeconds" signal, whose own
        // validator bounds it. Rejecting null here would make the inheritance unusable.
        var config = new ToolApprovalConfig { Enabled = true, Approvers = ["alice"], TimeoutSeconds = null };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Validate_NonPositiveTimeout_Fails(int seconds)
    {
        var config = new ToolApprovalConfig
        {
            Enabled = true,
            Approvers = ["alice"],
            TimeoutSeconds = seconds
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_TimeoutAboveEscalationServiceCeiling_Fails()
    {
        // The escalation service rejects a longer window by throwing, which the router reports as a
        // block — so the ceiling must be enforced here, where it is still a startup error.
        var config = new ToolApprovalConfig
        {
            Enabled = true,
            Approvers = ["alice"],
            TimeoutSeconds = EscalationRequestInvariants.MaxTimeoutSeconds + 1
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_TimeoutAtEscalationServiceCeiling_NoErrors()
    {
        // The boundary itself is accepted — an off-by-one here would reject a legal configuration.
        var config = new ToolApprovalConfig
        {
            Enabled = true,
            Approvers = ["alice"],
            TimeoutSeconds = EscalationRequestInvariants.MaxTimeoutSeconds
        };

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    private static ToolApprovalConfig CreateValidConfig() => new()
    {
        Enabled = true,
        Approvers = ["alice", "bob"],
        TimeoutSeconds = 120,
        CriticalAtBlastRadius = "Critical"
    };

    // ToolApprovalConfig is init-only, so a "mutate one field" test rebuilds rather than assigns.
    private static ToolApprovalConfig Rebuild(ToolApprovalConfig source, string criticalAtBlastRadius) => new()
    {
        Enabled = source.Enabled,
        Approvers = source.Approvers,
        TimeoutSeconds = source.TimeoutSeconds,
        CriticalAtBlastRadius = criticalAtBlastRadius
    };
}
