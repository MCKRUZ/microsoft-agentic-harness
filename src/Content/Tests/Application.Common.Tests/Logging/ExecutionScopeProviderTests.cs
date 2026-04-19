using Application.Common.Logging;
using Domain.Common.Logging;
using FluentAssertions;
using Xunit;

namespace Application.Common.Tests.Logging;

public sealed class ExecutionScopeProviderTests
{
    [Fact]
    public void GetCurrentScope_NullProvider_ReturnsNull()
    {
        var result = ExecutionScopeProvider.GetCurrentScope(null);

        result.Should().BeNull();
    }

    [Fact]
    public void GetCurrentScope_NoExecutionScopesPushed_ReturnsNull()
    {
        var provider = new ExecutionScopeProvider();

        var result = ExecutionScopeProvider.GetCurrentScope(provider);

        result.Should().BeNull();
    }

    [Fact]
    public void GetCurrentScope_SingleScope_ReturnsAllProperties()
    {
        var provider = new ExecutionScopeProvider();
        using var _ = provider.Push(new ExecutionScope(
            ExecutorId: "main",
            ParentExecutorId: null,
            CorrelationId: "corr-123",
            StepNumber: 1,
            OperationName: "planning"));

        var result = ExecutionScopeProvider.GetCurrentScope(provider);

        result.Should().NotBeNull();
        result!.ExecutorId.Should().Be("main");
        result.CorrelationId.Should().Be("corr-123");
        result.StepNumber.Should().Be(1);
        result.OperationName.Should().Be("planning");
    }

    [Fact]
    public void GetCurrentScope_NestedScopes_InnerOverridesOuter()
    {
        var provider = new ExecutionScopeProvider();
        using var outer = provider.Push(new ExecutionScope(
            ExecutorId: "main",
            CorrelationId: "corr-outer"));
        using var inner = provider.Push(new ExecutionScope(
            ExecutorId: "research",
            ParentExecutorId: "main"));

        var result = ExecutionScopeProvider.GetCurrentScope(provider);

        result.Should().NotBeNull();
        result!.ExecutorId.Should().Be("research", "inner scope overrides");
        result.ParentExecutorId.Should().Be("main");
        result.CorrelationId.Should().Be("corr-outer", "outer property preserved when not overridden");
    }

    [Fact]
    public void GetCurrentScope_NonExecutionScope_Ignored()
    {
        var provider = new ExecutionScopeProvider();
        using var _ = provider.Push("This is just a string scope");

        var result = ExecutionScopeProvider.GetCurrentScope(provider);

        result.Should().BeNull();
    }

    [Fact]
    public void GetCurrentScope_MixedScopes_OnlyExtractsExecutionScopes()
    {
        var provider = new ExecutionScopeProvider();
        using var s1 = provider.Push("string scope");
        using var s2 = provider.Push(new ExecutionScope(ExecutorId: "agent-1"));
        using var s3 = provider.Push(new Dictionary<string, object> { ["key"] = "value" });

        var result = ExecutionScopeProvider.GetCurrentScope(provider);

        result.Should().NotBeNull();
        result!.ExecutorId.Should().Be("agent-1");
    }

    [Fact]
    public void GetCurrentScope_AfterDispose_ScopeNoLongerVisible()
    {
        var provider = new ExecutionScopeProvider();

        using (provider.Push(new ExecutionScope(ExecutorId: "temp")))
        {
            var during = ExecutionScopeProvider.GetCurrentScope(provider);
            during.Should().NotBeNull();
        }

        var after = ExecutionScopeProvider.GetCurrentScope(provider);
        after.Should().BeNull();
    }

    [Fact]
    public void Push_ReturnsDisposable()
    {
        var provider = new ExecutionScopeProvider();

        var disposable = provider.Push(new ExecutionScope(ExecutorId: "test"));

        disposable.Should().NotBeNull();
        disposable!.Dispose();
    }

    [Fact]
    public void ForEachScope_PropagatesAllScopeObjects()
    {
        var provider = new ExecutionScopeProvider();
        using var s1 = provider.Push("scope-1");
        using var s2 = provider.Push("scope-2");

        var scopes = new List<object?>();
        provider.ForEachScope((scope, state) => state.Add(scope), scopes);

        scopes.Should().HaveCount(2);
        scopes.Should().Contain("scope-1");
        scopes.Should().Contain("scope-2");
    }
}
