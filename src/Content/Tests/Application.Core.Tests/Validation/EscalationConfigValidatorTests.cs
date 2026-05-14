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
