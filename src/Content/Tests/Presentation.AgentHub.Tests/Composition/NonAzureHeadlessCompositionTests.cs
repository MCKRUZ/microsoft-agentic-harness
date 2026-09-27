using Application.Core.CQRS.Agents.RunConversation;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Configuration;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Presentation.AgentHub.Tests.Composition;

/// <summary>
/// Regression coverage for issue #591: a self-hosted, non-Azure deployment must boot the real
/// production composition root and serve a real conversation turn with zero Azure configuration
/// present. Guards specifically against an Azure-only service resolution dependency creeping back
/// into <c>BuildGlobalSolutionServices</c>/<c>GetServices</c> — see
/// <see cref="NonAzureHeadlessFactory"/>'s remarks for how the zero-Azure environment is set up.
/// </summary>
public sealed class NonAzureHeadlessCompositionTests : IClassFixture<NonAzureHeadlessFactory>
{
    private readonly NonAzureHeadlessFactory _factory;

    public NonAzureHeadlessCompositionTests(NonAzureHeadlessFactory factory) => _factory = factory;

    [Fact]
    public void Host_ComposesAndBoots_WithZeroAzureConfiguration()
    {
        // Program.cs applies ValidateOnBuild + ValidateScopes via ApplyValidationPolicy, so every
        // registered service — including every assembly-scanned MediatR handler — must be
        // constructible at boot. Accessing Services is what forces WebApplicationFactory to build
        // the host; a throw here means a hidden dependency this configuration cannot satisfy.
        var act = () => _factory.Services.GetRequiredService<IMediator>();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task HealthAi_ReportsEchoProviderAsHealthy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ai");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        doc.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        var aiCheck = doc.RootElement.GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "ai_provider");
        aiCheck.GetProperty("data").GetProperty("clientType").GetString().Should().Be("Echo");
    }

    [Fact]
    public async Task HealthSubsystems_ReportsZeroAzureConfigSources()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/subsystems");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var check = doc.RootElement.GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "composed_subsystems");
        var data = check.GetProperty("data");
        data.GetProperty("azureKeyVaultConfigSourceLoaded").GetBoolean().Should().BeFalse();
        data.GetProperty("azureAppConfigurationSourceLoaded").GetBoolean().Should().BeFalse();
        data.GetProperty("aiProvider").GetString().Should().Be("Echo");
    }

    [Fact]
    public async Task ConfigurationSources_ContainNoAzureProviders()
    {
        var report = _factory.Services.GetRequiredService<HarnessConfigSourceReport>();

        report.AzureKeyVaultLoaded.Should().BeFalse();
        report.AzureAppConfigurationLoaded.Should().BeFalse();
    }

    [Fact]
    public async Task AgentTurn_WithEchoClient_Succeeds()
    {
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new RunConversationCommand
        {
            AgentName = "default",
            UserMessages = ["Say hello."],
        };

        var result = await mediator.Send(command);

        result.Success.Should().BeTrue();
        result.FinalResponse.Should().NotBeNullOrWhiteSpace();
    }
}
