using Domain.Common.Config;
using Domain.Common.Config.AI.BundleExecution;
using Domain.Common.Config.AI.WorkflowSubmission;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Proves the execution API, as shipped, runs as many background runs at once as the class defaults say.
/// </summary>
/// <remarks>
/// The defaults on <see cref="BundleExecutionConfig"/> and <see cref="WorkflowSubmissionConfig"/> only
/// matter if nothing in the host's own configuration files replaces them. A value left in
/// <c>appsettings.json</c> would quietly keep the old limit for every host cloned from this template, and
/// a test of the class alone would still pass.
/// </remarks>
public sealed class ShippedDispatchLimitTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ShippedDispatchLimitTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void BackgroundBundleRunLimit_InShippedHost_IsTheClassDefault()
    {
        var config = _factory.Services.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;

        config.AI.BundleExecution.MaxConcurrentDispatchedBundleRuns
            .Should().Be(new BundleExecutionConfig().MaxConcurrentDispatchedBundleRuns);
    }

    [Fact]
    public void BackgroundWorkflowRunLimit_InShippedHost_IsTheClassDefault()
    {
        var config = _factory.Services.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;

        config.AI.WorkflowSubmission.MaxConcurrentDispatchedRuns
            .Should().Be(new WorkflowSubmissionConfig().MaxConcurrentDispatchedRuns);
    }

    [Fact]
    public void HostWideBundleStreamLimit_InShippedHost_IsTheClassDefault()
    {
        var config = _factory.Services.GetRequiredService<IOptionsMonitor<AppConfig>>().CurrentValue;

        config.AI.BundleExecution.MaxConcurrentStreams
            .Should().Be(new BundleExecutionConfig().MaxConcurrentStreams);
    }
}
