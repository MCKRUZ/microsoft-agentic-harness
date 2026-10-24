using Application.AI.Common.CQRS.Schedules;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.Tools;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// <see cref="ManageSchedulesTool"/> passes the caller's id as issued (#907). The schedule store matches owners
/// case-insensitively on its own, and learns an older schedule's issued owner from the id it is given; the tool used to
/// lower-case first, which settled that schedule's runs on the lower-cased form for good, and documents those runs
/// ingest would then be out of reach of erasure.
/// </summary>
public sealed class ManageSchedulesToolOwnerTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly AmbientRequestScope _ambient = new();

    private (ManageSchedulesTool Tool, IServiceProvider Request) Create(string? userId)
    {
        var scope = Mock.Of<IKnowledgeScope>(s => s.UserId == userId && s.TenantId == "Tenant-1");
        var services = new ServiceCollection()
            .AddSingleton(_mediator.Object)
            .AddSingleton(scope)
            .BuildServiceProvider();
        return (new ManageSchedulesTool(_ambient, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ManageSchedulesTool>.Instance), services);
    }

    [Fact]
    public async Task List_PassesTheCallersIdAsIssued()
    {
        ListSchedulesQuery? sent = null;
        _mediator.Setup(m => m.Send(It.IsAny<ListSchedulesQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Result<IReadOnlyList<ScheduleSummary>>>, CancellationToken>((q, _) => sent = (ListSchedulesQuery)q)
            .ReturnsAsync(Result<IReadOnlyList<ScheduleSummary>>.Success([]));
        var (tool, request) = Create(" Alice-Mixed ");

        using (_ambient.BeginScope(request))
            (await tool.ExecuteAsync("list", new Dictionary<string, object?>())).Success.Should().BeTrue();

        sent!.OwnerId.Should().Be(" Alice-Mixed ");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task ACallerWithNoIdentity_IsRefused(string? userId)
    {
        var (tool, request) = Create(userId);

        using (_ambient.BeginScope(request))
            (await tool.ExecuteAsync("list", new Dictionary<string, object?>())).Success.Should().BeFalse();

        _mediator.VerifyNoOtherCalls();
    }
}
