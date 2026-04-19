using Domain.Common.Workflow;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Workflow;

/// <summary>
/// Tests for <see cref="StateConfiguration"/> — transitions, validation,
/// terminal states, and defaults.
/// </summary>
public class StateConfigurationTests
{
    private static StateConfiguration CreateValidConfig() => new()
    {
        AllowedStatuses = ["not_started", "in_progress", "completed", "failed"],
        AllowedTransitions = new Dictionary<string, List<string>>
        {
            ["not_started"] = ["in_progress"],
            ["in_progress"] = ["completed", "failed"],
            ["failed"] = ["not_started"]
        },
        InitialStatus = "not_started",
        TerminalStates = ["completed"]
    };

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new StateConfiguration();

        config.AllowedStatuses.Should().BeEmpty();
        config.AllowedTransitions.Should().BeEmpty();
        config.InitialStatus.Should().Be("not_started");
        config.TerminalStates.Should().BeEmpty();
    }

    [Fact]
    public void CanTransition_ValidTransition_ReturnsTrue()
    {
        var config = CreateValidConfig();

        config.CanTransition("not_started", "in_progress").Should().BeTrue();
    }

    [Fact]
    public void CanTransition_InvalidTransition_ReturnsFalse()
    {
        var config = CreateValidConfig();

        config.CanTransition("not_started", "completed").Should().BeFalse();
    }

    [Fact]
    public void CanTransition_SameStatus_AlwaysReturnsTrue()
    {
        var config = CreateValidConfig();

        config.CanTransition("in_progress", "in_progress").Should().BeTrue();
    }

    [Fact]
    public void CanTransition_UnknownFromStatus_ReturnsFalse()
    {
        var config = CreateValidConfig();

        config.CanTransition("unknown", "in_progress").Should().BeFalse();
    }

    [Fact]
    public void IsTerminal_TerminalStatus_ReturnsTrue()
    {
        var config = CreateValidConfig();

        config.IsTerminal("completed").Should().BeTrue();
    }

    [Fact]
    public void IsTerminal_NonTerminalStatus_ReturnsFalse()
    {
        var config = CreateValidConfig();

        config.IsTerminal("in_progress").Should().BeFalse();
    }

    [Fact]
    public void IsValidStatus_ValidStatus_ReturnsTrue()
    {
        var config = CreateValidConfig();

        config.IsValidStatus("in_progress").Should().BeTrue();
    }

    [Fact]
    public void IsValidStatus_InvalidStatus_ReturnsFalse()
    {
        var config = CreateValidConfig();

        config.IsValidStatus("cancelled").Should().BeFalse();
    }

    [Fact]
    public void GetValidTransitions_ExistingStatus_ReturnsTransitions()
    {
        var config = CreateValidConfig();

        var transitions = config.GetValidTransitions("in_progress");

        transitions.Should().Contain("completed");
        transitions.Should().Contain("failed");
    }

    [Fact]
    public void GetValidTransitions_UnknownStatus_ReturnsEmpty()
    {
        var config = CreateValidConfig();

        config.GetValidTransitions("unknown").Should().BeEmpty();
    }

    [Fact]
    public void GetValidTransitions_ReturnsNewList()
    {
        var config = CreateValidConfig();

        var a = config.GetValidTransitions("in_progress");
        var b = config.GetValidTransitions("in_progress");

        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void Validate_ValidConfig_ReturnsNoErrors()
    {
        var config = CreateValidConfig();

        var errors = config.Validate();

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_InitialStatusNotAllowed_ReturnsError()
    {
        var config = CreateValidConfig();
        config.InitialStatus = "unknown";

        var errors = config.Validate();

        errors.Should().Contain(e => e.Contains("Initial status 'unknown'"));
    }

    [Fact]
    public void Validate_TransitionSourceNotAllowed_ReturnsError()
    {
        var config = new StateConfiguration
        {
            AllowedStatuses = ["a", "b"],
            AllowedTransitions = new Dictionary<string, List<string>>
            {
                ["invalid_source"] = ["a"]
            },
            InitialStatus = "a"
        };

        var errors = config.Validate();

        errors.Should().Contain(e => e.Contains("Transition source 'invalid_source'"));
    }

    [Fact]
    public void Validate_TransitionTargetNotAllowed_ReturnsError()
    {
        var config = new StateConfiguration
        {
            AllowedStatuses = ["a", "b"],
            AllowedTransitions = new Dictionary<string, List<string>>
            {
                ["a"] = ["invalid_target"]
            },
            InitialStatus = "a"
        };

        var errors = config.Validate();

        errors.Should().Contain(e => e.Contains("Transition target 'invalid_target'"));
    }

    [Fact]
    public void Validate_TerminalStateWithOutgoingTransitions_ReturnsError()
    {
        var config = new StateConfiguration
        {
            AllowedStatuses = ["a", "b"],
            AllowedTransitions = new Dictionary<string, List<string>>
            {
                ["a"] = ["b"]
            },
            InitialStatus = "a",
            TerminalStates = ["a"]
        };

        var errors = config.Validate();

        errors.Should().Contain(e => e.Contains("Terminal state 'a' has outgoing transitions"));
    }
}
