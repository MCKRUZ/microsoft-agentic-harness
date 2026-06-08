using System.Reflection;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Infrastructure.AI.Tests.Telemetry;

/// <summary>
/// Verifies <c>DependencyInjection.RegisterContentCaptureServices</c> wires the
/// content-capture pipeline: the default redaction filter, the capture policy,
/// and the startup validator hosted service. The filter and policy use
/// <c>TryAdd</c> semantics so a consumer can register custom implementations
/// before <c>AddInfrastructureAIDependencies</c> runs. The helper is private, so
/// it is invoked via reflection — the public entry point pulls in the full
/// Infrastructure.AI graph, which is heavier than this unit needs.
/// </summary>
public sealed class ContentCaptureDependencyInjectionTests
{
    private static readonly MethodInfo RegisterMethod =
        typeof(Infrastructure.AI.DependencyInjection)
            .GetMethod("RegisterContentCaptureServices", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void Register(IServiceCollection services)
        => RegisterMethod.Invoke(null, [services]);

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ContentCaptureTestConfig.Monitor(ContentCaptureTestConfig.AllOn()));
        return services;
    }

    [Fact]
    public void Register_ResolvesRedactionFilterAndPolicy()
    {
        var services = BaseServices();

        Register(services);
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IContentRedactionFilter>().Should().BeOfType<DefaultContentRedactionFilter>();
        sp.GetRequiredService<IContentCapturePolicy>().Should().BeOfType<ContentCapturePolicy>();
    }

    [Fact]
    public void Register_RegistersStartupValidatorAsHostedService()
    {
        var services = BaseServices();

        Register(services);
        var sp = services.BuildServiceProvider();

        sp.GetServices<IHostedService>()
            .Should().ContainSingle(h => h is ContentCaptureStartupValidator);
    }

    [Fact]
    public void Register_TryAddSemantics_HonoursConsumerFilterOverride()
    {
        var services = BaseServices();
        var custom = new CustomFilter();
        services.AddSingleton<IContentRedactionFilter>(custom);

        Register(services);
        var sp = services.BuildServiceProvider();

        // TryAddSingleton must not clobber the consumer-registered filter.
        sp.GetRequiredService<IContentRedactionFilter>().Should().BeSameAs(custom);
    }

    private sealed class CustomFilter : IContentRedactionFilter
    {
        public string Redact(string? content, IReadOnlyList<Domain.AI.Telemetry.Redaction.RedactionCategory> categories)
            => content ?? string.Empty;
    }
}
