using System.Diagnostics;
using Application.AI.Common.OpenTelemetry.Processors;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.OpenTelemetry.Processors;

/// <summary>
/// Tests for <see cref="ConversationSpanProcessor"/> covering baggage propagation
/// to span tags for harness and AI framework sources, and skipping unrelated sources.
/// </summary>
public class ConversationSpanProcessorTests : IDisposable
{
    private readonly ActivitySource _harnessSource = new(AppSourceNames.AgenticHarness);
    private readonly ActivitySource _mediatrSource = new(AppSourceNames.AgenticHarnessMediatR);
    private readonly ActivitySource _aiFrameworkSource = new("Microsoft.Agents.AI.Test");
    private readonly ActivitySource _extensionsSource = new("Microsoft.Extensions.AI.Test");
    private readonly ActivitySource _skSource = new("Microsoft.SemanticKernel.Test");
    private readonly ActivitySource _unrelatedSource = new("Unrelated.Source");
    private readonly ActivityListener _listener;
    private readonly ConversationSpanProcessor _processor = new();

    public ConversationSpanProcessorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _harnessSource.Dispose();
        _mediatrSource.Dispose();
        _aiFrameworkSource.Dispose();
        _extensionsSource.Dispose();
        _skSource.Dispose();
        _unrelatedSource.Dispose();
        _processor.Dispose();
    }

    [Fact]
    public void OnStart_HarnessSource_CopiesBaggageToTags()
    {
        using var parent = _harnessSource.StartActivity("parent");
        parent.Should().NotBeNull();
        parent!.SetBaggage(AgentConventions.ConversationId, "conv-123");
        parent.SetBaggage(AgentConventions.TurnIndex, "5");
        parent.SetBaggage(AgentConventions.Name, "planner");

        using var child = _harnessSource.StartActivity("child");
        child.Should().NotBeNull();

        _processor.OnStart(child!);

        child.GetTagItem(AgentConventions.ConversationId).Should().Be("conv-123");
        child.GetTagItem(AgentConventions.TurnIndex).Should().Be("5");
        child.GetTagItem(AgentConventions.Name).Should().Be("planner");
    }

    [Fact]
    public void OnStart_MediatRSource_CopiesBaggageToTags()
    {
        using var parent = _mediatrSource.StartActivity("parent");
        parent.Should().NotBeNull();
        parent!.SetBaggage(AgentConventions.ConversationId, "conv-456");

        using var child = _mediatrSource.StartActivity("child");
        child.Should().NotBeNull();

        _processor.OnStart(child!);

        child.GetTagItem(AgentConventions.ConversationId).Should().Be("conv-456");
    }

    [Fact]
    public void OnStart_AiFrameworkSource_CopiesBaggageToTags()
    {
        using var parent = _aiFrameworkSource.StartActivity("parent");
        parent.Should().NotBeNull();
        parent!.SetBaggage(AgentConventions.Name, "reviewer");

        using var child = _aiFrameworkSource.StartActivity("child");
        child.Should().NotBeNull();

        _processor.OnStart(child!);

        child.GetTagItem(AgentConventions.Name).Should().Be("reviewer");
    }

    [Fact]
    public void OnStart_ExtensionsAISource_CopiesBaggageToTags()
    {
        using var parent = _extensionsSource.StartActivity("parent");
        parent.Should().NotBeNull();
        parent!.SetBaggage(AgentConventions.Name, "agent");

        using var child = _extensionsSource.StartActivity("child");
        child.Should().NotBeNull();

        _processor.OnStart(child!);

        child.GetTagItem(AgentConventions.Name).Should().Be("agent");
    }

    [Fact]
    public void OnStart_SemanticKernelSource_CopiesBaggageToTags()
    {
        using var parent = _skSource.StartActivity("parent");
        parent.Should().NotBeNull();
        parent!.SetBaggage(AgentConventions.ConversationId, "sk-conv");

        using var child = _skSource.StartActivity("child");
        child.Should().NotBeNull();

        _processor.OnStart(child!);

        child.GetTagItem(AgentConventions.ConversationId).Should().Be("sk-conv");
    }

    [Fact]
    public void OnStart_UnrelatedSource_DoesNotCopyBaggage()
    {
        using var parent = _unrelatedSource.StartActivity("parent");
        parent.Should().NotBeNull();
        parent!.SetBaggage(AgentConventions.Name, "should-not-appear");

        using var child = _unrelatedSource.StartActivity("child");
        child.Should().NotBeNull();

        _processor.OnStart(child!);

        child.GetTagItem(AgentConventions.Name).Should().BeNull();
    }

    [Fact]
    public void OnStart_NoBaggage_DoesNotSetTags()
    {
        using var activity = _harnessSource.StartActivity("test");
        activity.Should().NotBeNull();

        _processor.OnStart(activity!);

        activity.GetTagItem(AgentConventions.ConversationId).Should().BeNull();
        activity.GetTagItem(AgentConventions.TurnIndex).Should().BeNull();
        activity.GetTagItem(AgentConventions.Name).Should().BeNull();
    }
}
