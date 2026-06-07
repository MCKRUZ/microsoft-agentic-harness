using Application.AI.Common.Interfaces.Egress;
using Domain.AI.Egress;
using FluentAssertions;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Tests.Egress.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Egress;

public sealed class EgressPolicyDelegatingHandlerTests
{
    [Fact]
    public async Task NoIdentityInScope_BlocksAndAudits()
    {
        var audit = new InMemoryEgressAuditWriter();
        var ambient = new FakeAmbientRequestScope(identity: null);
        var rootServices = BuildRootServices(allowAll: true);

        var (handler, inner) = NewHandler(audit, ambient, rootServices);

        using var client = new HttpClient(handler);

        var act = async () => await client.GetAsync("https://api.github.com/");
        await act.Should().ThrowAsync<EgressBlockedException>();

        inner.CallCount.Should().Be(0);
        audit.Entries.Should().HaveCount(1);
        audit.Entries.TryPeek(out var entry).Should().BeTrue();
        entry.Decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task DenyDecision_ThrowsEgressBlockedExceptionAndAudits()
    {
        var audit = new InMemoryEgressAuditWriter();
        var ambient = new FakeAmbientRequestScope(TestIdentity.Default);
        var rootServices = BuildRootServices(allowAll: false); // empty allowlist → deny

        var (handler, inner) = NewHandler(audit, ambient, rootServices);

        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<EgressBlockedException>(
            () => client.GetAsync("https://api.github.com/"));

        inner.CallCount.Should().Be(0);
        audit.Entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task AllowDecision_DelegatesToInnerAndAudits()
    {
        var audit = new InMemoryEgressAuditWriter();
        var ambient = new FakeAmbientRequestScope(TestIdentity.Default);
        var rootServices = BuildRootServices(allowAll: true);

        var (handler, inner) = NewHandler(audit, ambient, rootServices);

        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://api.github.com/");

        response.IsSuccessStatusCode.Should().BeTrue();
        inner.CallCount.Should().Be(1);
        audit.Entries.Should().HaveCount(1);
        audit.Entries.TryPeek(out var entry).Should().BeTrue();
        entry.Decision.Allowed.Should().BeTrue();
    }

    private static (EgressPolicyDelegatingHandler Handler, StubHttpMessageHandler Inner) NewHandler(
        InMemoryEgressAuditWriter audit,
        FakeAmbientRequestScope ambient,
        IServiceProvider rootServices)
    {
        var inner = new StubHttpMessageHandler();
        var handler = new EgressPolicyDelegatingHandler(
            rootServices,
            ambient,
            audit,
            NullLogger<EgressPolicyDelegatingHandler>.Instance,
            TimeProvider.System)
        {
            InnerHandler = inner
        };
        return (handler, inner);
    }

    private static IServiceProvider BuildRootServices(bool allowAll)
    {
        var entries = allowAll
            ? new EgressAllowlistEntry[]
              {
                  new()
                  {
                      Host = "api.github.com",
                      Schemes = ["https"],
                      Ports = [443]
                  }
              }
            : [];

        var services = new ServiceCollection();
        var policy = new DefaultEgressPolicy(entries, NullLogger<DefaultEgressPolicy>.Instance, TimeProvider.System);
        services.AddSingleton<IEgressPolicy>(policy);
        services.AddSingleton<IEgressPolicyResolver>(new DefaultEgressPolicyResolver(policy));
        return services.BuildServiceProvider();
    }
}
