using Domain.Common;
using Domain.Common.Extensions;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Extensions;

/// <summary>
/// Regression tests for the solution-review fix (finding 14): <c>PropagateFailure</c> must
/// preserve the governance failure types <see cref="ResultFailureType.PermissionRequired"/>,
/// <see cref="ResultFailureType.GovernanceBlocked"/>, and <see cref="ResultFailureType.PendingApproval"/>
/// when propagating a failure through <see cref="ResultExtensions.Map{T,TOut}"/> /
/// <see cref="ResultExtensions.Bind{T,TOut}"/>. The previous implementation lacked switch arms for
/// these three types, so they fell through the discard arm and were silently downgraded to
/// <see cref="ResultFailureType.General"/> — violating the class's documented invariant that all
/// methods "preserve the original ResultFailureType when propagating failures".
/// </summary>
public class ResultExtensionsSolutionReviewFixTests
{
    [Fact]
    public void Map_PermissionRequiredFailure_PreservesFailureType()
    {
        var result = Result<int>.PermissionRequired("confirm before delete");

        var mapped = result.Map(v => v.ToString());

        mapped.IsSuccess.Should().BeFalse();
        mapped.FailureType.Should().Be(ResultFailureType.PermissionRequired);
        mapped.Errors.Should().ContainSingle().Which.Should().Be("confirm before delete");
    }

    [Fact]
    public void Map_GovernanceBlockedFailure_PreservesFailureType()
    {
        var result = Result<int>.GovernanceBlocked("blocked by policy");

        var mapped = result.Map(v => v.ToString());

        mapped.IsSuccess.Should().BeFalse();
        mapped.FailureType.Should().Be(ResultFailureType.GovernanceBlocked);
        mapped.Errors.Should().ContainSingle().Which.Should().Be("blocked by policy");
    }

    [Fact]
    public void Map_PendingApprovalFailure_PreservesFailureType()
    {
        var result = Result<int>.PendingApproval("escalation-123");

        var mapped = result.Map(v => v.ToString());

        mapped.IsSuccess.Should().BeFalse();
        mapped.FailureType.Should().Be(ResultFailureType.PendingApproval);
        mapped.Errors.Should().ContainSingle().Which.Should().Be("escalation-123");
    }

    [Fact]
    public void Bind_GovernanceBlockedFailure_PreservesFailureType()
    {
        var result = Result<int>.GovernanceBlocked("blocked by policy");

        var bound = result.Bind(v => Result<string>.Success(v.ToString()));

        bound.IsSuccess.Should().BeFalse();
        bound.FailureType.Should().Be(ResultFailureType.GovernanceBlocked);
    }

    [Fact]
    public async Task BindAsync_PendingApprovalFailure_PreservesFailureType()
    {
        var result = Result<int>.PendingApproval("escalation-456");

        var bound = await result.BindAsync(v => Task.FromResult(Result<string>.Success(v.ToString())));

        bound.IsSuccess.Should().BeFalse();
        bound.FailureType.Should().Be(ResultFailureType.PendingApproval);
    }
}
