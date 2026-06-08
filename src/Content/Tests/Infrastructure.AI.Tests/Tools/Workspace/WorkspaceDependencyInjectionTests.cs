using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Interfaces.Workspace;
using Domain.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Tools.Workspace;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.AI.Tests.Tools.Workspace;

/// <summary>
/// Verifies <see cref="WorkspaceDependencyInjection.AddWorkspaceSkillTools"/>
/// registers every keyed-DI binding the SKILL.md manifest depends on.
/// </summary>
public sealed class WorkspaceDependencyInjectionTests
{
    [Fact]
    public void AddWorkspaceSkillTools_RegistersAllFiveToolsByKeyedName()
    {
        var services = new ServiceCollection();

        // Dependencies the workspace tools consume that aren't part of this DI module.
        services.AddSingleton<IMediator>(_ => new NoOpMediator());
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Process, new NoOpSandbox());

        services.AddWorkspaceSkillTools();
        var sp = services.BuildServiceProvider();

        sp.GetKeyedService<ITool>("read_file").Should().BeOfType<WorkspaceReadFileTool>();
        sp.GetKeyedService<ITool>("write_file").Should().BeOfType<WorkspaceWriteFileTool>();
        sp.GetKeyedService<ITool>("list_files").Should().BeOfType<WorkspaceListFilesTool>();
        sp.GetKeyedService<ITool>("run_tests").Should().BeOfType<WorkspaceRunTestsTool>();
        sp.GetKeyedService<ITool>("run_lint").Should().BeOfType<WorkspaceRunLintTool>();
    }

    [Fact]
    public void AddWorkspaceSkillTools_RegistersAccessorAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMediator>(_ => new NoOpMediator());
        services.AddKeyedSingleton<ISandboxExecutor>(SandboxIsolationLevel.Process, new NoOpSandbox());

        services.AddWorkspaceSkillTools();
        var sp = services.BuildServiceProvider();

        var a = sp.GetRequiredService<IWorkspaceContextAccessor>();
        var b = sp.GetRequiredService<IWorkspaceContextAccessor>();

        a.Should().BeSameAs(b, "accessor backing store is per-async-flow, not per-DI-scope");
    }

    private sealed class NoOpMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
            => throw new NotImplementedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;
    }

    private sealed class NoOpSandbox : ISandboxExecutor
    {
        public Task<SandboxExecutionResult> ExecuteAsync(SandboxExecutionRequest request, CancellationToken ct)
            => Task.FromResult(new SandboxExecutionResult { Success = true, ExitCode = 0, Output = "" });
    }
}
