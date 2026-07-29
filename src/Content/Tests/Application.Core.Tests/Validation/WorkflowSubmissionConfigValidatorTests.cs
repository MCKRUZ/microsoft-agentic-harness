using Application.Core.Validation;
using Domain.Common.Config.AI.WorkflowSubmission;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="WorkflowSubmissionConfigValidator"/>.
/// </summary>
/// <remarks>
/// The property that matters is that the shipped defaults boot. Every rule is unconditional, so if any
/// default were outside its own rule, every host in the solution would fail to start — including the
/// ones that never enable workflow submission at all.
/// </remarks>
public sealed class WorkflowSubmissionConfigValidatorTests
{
    private readonly WorkflowSubmissionConfigValidator _validator = new();

    [Fact]
    public void Validate_ShippedDefaults_AreValid()
    {
        _validator.Validate(new WorkflowSubmissionConfig()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxRequestBytes))]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxSteps))]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxEdges))]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxFanOutPerStep))]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxSubPlanNestingDepth))]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxStringFieldLength))]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxParallelSteps))]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxTokensPerStep))]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxTopK))]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxStoredWorkflowsPerOwner))]
    public void Validate_NonPositiveIntegerCap_IsRejected(string property)
    {
        var config = new WorkflowSubmissionConfig();
        typeof(WorkflowSubmissionConfig).GetProperty(property)!.SetValue(config, 0);

        var result = _validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == property);
    }

    [Theory]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxPlanTimeout))]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxStepTimeout))]
    [InlineData(nameof(WorkflowSubmissionConfig.MaxHumanGateTimeout))]
    public void Validate_NonPositiveTimeoutCeiling_IsRejected(string property)
    {
        var config = new WorkflowSubmissionConfig();
        typeof(WorkflowSubmissionConfig).GetProperty(property)!.SetValue(config, TimeSpan.Zero);

        var result = _validator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == property);
    }

    [Fact]
    public void Validate_ZeroRetryCeiling_IsAllowed()
    {
        // Zero is a meaningful policy — submissions may not request any retries — unlike the other
        // caps, where zero would reject every workflow.
        var config = new WorkflowSubmissionConfig { MaxRetriesPerStep = 0 };

        _validator.Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_NegativeRetryCeiling_IsRejected()
    {
        var config = new WorkflowSubmissionConfig { MaxRetriesPerStep = -1 };

        _validator.Validate(config).IsValid.Should().BeFalse();
    }
}
