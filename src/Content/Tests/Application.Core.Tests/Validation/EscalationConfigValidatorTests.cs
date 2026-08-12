using Application.Core.Validation;
using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="EscalationConfigValidator"/>.
/// Pattern: CreateValidConfig() baseline, mutate one field per test.
/// </summary>
public class EscalationConfigValidatorTests
{
    private readonly EscalationConfigValidator _validator = new();

    [Fact]
    public async Task Validate_ValidConfig_NoErrors()
    {
        var config = CreateValidConfig();

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("oid")]
    [InlineData("sub")]
    [InlineData("preferred_username")]
    [InlineData("upn")]
    public async Task Validate_AllowlistedApproverClaimType_NoErrors(string claimType)
    {
        var config = CreateValidConfig();
        config.ApproverClaimType = claimType;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]                       // empty is not an identity claim
    [InlineData("   ")]                    // whitespace neither
    [InlineData("name")]                   // user-editable display name
    [InlineData("email")]                  // unverified, user-editable
    [InlineData("Preferred_Username")]     // allowlist is exact-match: claim types are case-sensitive
    public async Task Validate_NonAllowlistedApproverClaimType_HasError(string claimType)
    {
        // Roster authorization may only ever be driven by issuer-asserted identity claims; a
        // claim a user can self-edit (display name, unverified email) must fail at startup, not
        // silently become the approval identity.
        var config = CreateValidConfig();
        config.ApproverClaimType = claimType;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ApproverClaimType");
    }

    [Fact]
    public async Task Validate_NonAllowlistedClaimTypeWhileDisabled_StillHasError()
    {
        // ApproverClaimType is deliberately NOT gated on Enabled, unlike its sibling rules. The
        // change-proposal decision API reads the same setting to stamp reviewer identity on its
        // audit records and mounts independently of this flag — which defaults to false. Gating
        // the allowlist here would let the DEFAULT deployment run on an unvalidated claim type,
        // so an operator could bind approval identity to a user-editable claim and get
        // attacker-chosen strings recorded as the deciding reviewer.
        var config = CreateValidConfig();
        config.Enabled = false;
        config.PriorityLevels.Clear();
        config.ApproverClaimType = "email";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ApproverClaimType");
    }

    [Fact]
    public async Task Validate_DefaultConfigWhileDisabled_NoErrors()
    {
        // The counterweight to the rule above: always-on claim validation must not break hosts
        // running on class defaults. The default (preferred_username) is allowlisted, so a
        // host that never touches escalation config still boots.
        var config = new EscalationConfig();

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_NegativeTimeout_HasError()
    {
        var config = CreateValidConfig();
        config.DefaultTimeoutSeconds = -1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "DefaultTimeoutSeconds");
    }

    [Fact]
    public async Task Validate_ZeroTimeout_Allowed()
    {
        var config = CreateValidConfig();
        config.DefaultTimeoutSeconds = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_NegativePriorityTimeout_HasError()
    {
        var config = CreateValidConfig();
        config.PriorityLevels["Blocking"].TimeoutSeconds = -5;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("TimeoutSeconds"));
    }

    [Fact]
    public async Task Validate_EmptyPriorityLevels_HasError()
    {
        var config = CreateValidConfig();
        config.PriorityLevels = new Dictionary<string, EscalationPriorityConfig>();

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "PriorityLevels");
    }

    [Fact]
    public async Task Validate_InvalidTimeoutAction_HasError()
    {
        var config = CreateValidConfig();
        config.DefaultTimeoutAction = "Explode";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "DefaultTimeoutAction");
    }

    [Fact]
    public async Task Validate_InvalidApprovalStrategy_HasError()
    {
        var config = CreateValidConfig();
        config.DefaultApprovalStrategy = "MajorityRules";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "DefaultApprovalStrategy");
    }

    [Fact]
    public async Task Validate_EmptyAuditStoragePath_HasError()
    {
        var config = CreateValidConfig();
        config.AuditStoragePath = "";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "AuditStoragePath");
    }

    [Fact]
    public async Task Validate_ApproveOnTimeoutWithACriticalPriorityLevel_Fails()
    {
        // Boot-time form of the invariant EscalationRequestInvariants.TryValidate enforces per
        // request. DefaultTimeoutAction has no per-priority override — it applies globally — so
        // this configuration means every Critical escalation auto-approves on timeout unless
        // some caller overrides TimeoutAction explicitly.
        var config = CreateValidConfig();
        config.DefaultTimeoutAction = "Approve";
        // CreateValidConfig() already configures a "Critical" priority level.

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "DefaultTimeoutAction");
    }

    [Fact]
    public async Task Validate_ApproveOnTimeoutWithNoCriticalPriorityLevel_Passes()
    {
        // Mutation control: Approve-on-timeout alone must not fail validation — only the
        // pairing with a configured Critical priority level.
        var config = CreateValidConfig();
        config.DefaultTimeoutAction = "Approve";
        config.PriorityLevels.Remove("Critical");

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    private static EscalationConfig CreateValidConfig() => new()
    {
        Enabled = true,
        DefaultTimeoutSeconds = 300,
        DefaultTimeoutAction = "DenyAndEscalate",
        DefaultApprovalStrategy = "AnyOf",
        PriorityLevels = new Dictionary<string, EscalationPriorityConfig>
        {
            ["Informational"] = new() { TimeoutSeconds = 600, Async = true },
            ["Blocking"] = new() { TimeoutSeconds = 300 },
            ["Critical"] = new() { TimeoutSeconds = 120, EscalateToAll = true }
        }
    };
}
