using Application.AI.Common.Interfaces.Governance;
using Application.Core.CQRS.Autonomy;
using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Autonomy;

/// <summary>
/// Tests for <see cref="GetAutonomyTierQueryHandler"/>: the tier read reflects exactly what the
/// shared <see cref="IAutonomyTierResolver"/> reports (config parity), unknown subagent type
/// names map to <c>NotFound</c>, and the read path performs no calls beyond the single resolver
/// lookup (side-effect-free).
/// </summary>
public sealed class GetAutonomyTierQueryHandlerTests
{
    private static GetAutonomyTierQueryHandler BuildHandler(Mock<IAutonomyTierResolver> resolver) =>
        new(resolver.Object, NullLogger<GetAutonomyTierQueryHandler>.Instance);

    [Theory]
    [InlineData(SubagentType.Explore, AutonomyLevel.Restricted)]
    [InlineData(SubagentType.Plan, AutonomyLevel.Supervised)]
    [InlineData(SubagentType.Execute, AutonomyLevel.Autonomous)]
    public async Task Handle_KnownSubagentType_ReturnsTierFromSharedResolver(
        SubagentType subagentType, AutonomyLevel configuredTier)
    {
        // Strict: any call other than the single expected Resolve throws, proving the read
        // path touches nothing else.
        var resolver = new Mock<IAutonomyTierResolver>(MockBehavior.Strict);
        resolver.Setup(r => r.Resolve(subagentType)).Returns(configuredTier);
        var handler = BuildHandler(resolver);

        var result = await handler.Handle(
            new GetAutonomyTierQuery { SubagentType = subagentType.ToString() },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.SubagentType.Should().Be(subagentType);
        result.Value.Tier.Should().Be(configuredTier,
            "the endpoint must report exactly what the enforcement path's resolver reports");
        resolver.Verify(r => r.Resolve(subagentType), Times.Once);
        resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handle_SubagentTypeName_IsCaseInsensitive()
    {
        var resolver = new Mock<IAutonomyTierResolver>(MockBehavior.Strict);
        resolver.Setup(r => r.Resolve(SubagentType.Explore)).Returns(AutonomyLevel.Supervised);
        var handler = BuildHandler(resolver);

        var result = await handler.Handle(
            new GetAutonomyTierQuery { SubagentType = "explore" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.SubagentType.Should().Be(SubagentType.Explore);
    }

    [Theory]
    [InlineData("Nonexistent")]
    [InlineData("2")] // numeric forms are rejected even when they match a defined value
    [InlineData("999")]
    [InlineData("")]
    public async Task Handle_UnknownSubagentType_ReturnsNotFoundWithoutTouchingResolver(string name)
    {
        // Strict with zero setups: resolving an unknown type must not reach the resolver at all.
        var resolver = new Mock<IAutonomyTierResolver>(MockBehavior.Strict);
        var handler = BuildHandler(resolver);

        var result = await handler.Handle(
            new GetAutonomyTierQuery { SubagentType = name },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound,
            "an unknown subagent type must map to 404 at the HTTP boundary");
        resolver.VerifyNoOtherCalls();
    }
}
