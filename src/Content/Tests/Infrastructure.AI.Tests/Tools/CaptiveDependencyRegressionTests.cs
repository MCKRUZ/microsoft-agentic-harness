using Application.AI.Common.Interfaces.GitOps;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Orchestration.Magentic;
using Infrastructure.AI.Tests.Tools.Workspace.Support;
using Infrastructure.AI.Tools.GitOps;
using Infrastructure.AI.Tools.Workspace;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Regression tests for captive-dependency bugs flushed out by enabling
/// <c>ValidateScopes</c> in the AgentHub host (audit finding H2 follow-up).
/// Keyed-SINGLETON tool factories and singleton services must never resolve
/// scope-bound services (<c>ISandboxExecutor</c> is keyed SCOPED; a dispatched
/// <c>IMediator</c> pipeline ctor-injects the scoped <c>IAgentExecutionContext</c>)
/// from the root provider — they must create a scope per execution via
/// <see cref="IServiceScopeFactory"/>, the same pattern
/// <c>WorkMemorySynthesisBackgroundService</c> uses.
/// </summary>
/// <remarks>
/// The <see cref="IMediator"/> test double is registered SCOPED. Production
/// registers MediatR transient, but any <c>Send</c> resolved from the root
/// provider constructs the pipeline behaviors from the root, and six behaviors
/// ctor-inject the scoped <c>IAgentExecutionContext</c> — so under scope
/// validation a root-bound mediator dispatch throws. The scoped double encodes
/// that constraint ("IMediator must be consumed from a request scope") in
/// miniature without booting the full MediatR pipeline.
/// </remarks>
public sealed class CaptiveDependencyRegressionTests
{
    private static ServiceProvider BuildWorkspaceProvider(
        ISandboxExecutor sandbox,
        IMediator mediator)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Production registrations under test.
        services.AddWorkspaceSkillTools();

        // Production lifetime (DependencyInjection.Planner.cs): the sandbox
        // executor is keyed SCOPED on the isolation level.
        services.AddKeyedScoped<ISandboxExecutor>(
            SandboxIsolationLevel.Process, (_, _) => sandbox);

        services.AddScoped(_ => mediator);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
    }

    [Fact]
    public void RunTestsTool_ResolvesFromRoot_UnderScopeValidation()
    {
        using var provider = BuildWorkspaceProvider(
            Mock.Of<ISandboxExecutor>(), Mock.Of<IMediator>());

        var act = () => provider.GetRequiredKeyedService<ITool>(WorkspaceRunTestsTool.ToolName);

        act.Should().NotThrow(
            "the keyed-singleton tool factory must not resolve the keyed-scoped ISandboxExecutor from the root provider");
    }

    [Fact]
    public void RunLintTool_ResolvesFromRoot_UnderScopeValidation()
    {
        using var provider = BuildWorkspaceProvider(
            Mock.Of<ISandboxExecutor>(), Mock.Of<IMediator>());

        var act = () => provider.GetRequiredKeyedService<ITool>(WorkspaceRunLintTool.ToolName);

        act.Should().NotThrow(
            "the keyed-singleton tool factory must not resolve the keyed-scoped ISandboxExecutor from the root provider");
    }

    [Fact]
    public void WriteFileTool_ResolvesFromRoot_UnderScopeValidation()
    {
        using var provider = BuildWorkspaceProvider(
            Mock.Of<ISandboxExecutor>(), Mock.Of<IMediator>());

        var act = () => provider.GetRequiredKeyedService<ITool>(WorkspaceWriteFileTool.ToolName);

        act.Should().NotThrow(
            "the keyed-singleton tool factory must not capture a root-bound IMediator");
    }

    [Fact]
    public async Task RunTestsTool_ExecutesThroughScopedSandboxExecutor()
    {
        var recorded = new List<SandboxExecutionRequest>();
        var sandboxMock = new Mock<ISandboxExecutor>();
        sandboxMock
            .Setup(s => s.ExecuteAsync(It.IsAny<SandboxExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SandboxExecutionRequest, CancellationToken>((req, _) => recorded.Add(req))
            .ReturnsAsync(new SandboxExecutionResult
            {
                Success = true,
                ExitCode = 0,
                Output = "5 tests passed",
            });

        using var provider = BuildWorkspaceProvider(sandboxMock.Object, Mock.Of<IMediator>());
        using var fx = new WorkspaceTestFixture(testCommand: "dotnet test");

        var accessor = provider.GetRequiredService<Application.AI.Common.Interfaces.Workspace.IWorkspaceContextAccessor>();
        using var workspaceScope = accessor.BeginScope(fx.Context);

        var tool = provider.GetRequiredKeyedService<ITool>(WorkspaceRunTestsTool.ToolName);
        var result = await tool.ExecuteAsync("run", new Dictionary<string, object?>());

        result.Success.Should().BeTrue();
        recorded.Should().ContainSingle("the tool must dispatch through the scoped executor resolved per execution");
    }

    [Fact]
    public void GitOpsRemediationDispatcher_ResolvesFromRoot_UnderScopeValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Mock.Of<IMediator>());

        // Production registrations under test (DependencyInjection.GitOps.cs).
        services.AddGitOpsSkillTools();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        var act = () => provider.GetRequiredService<IGitOpsRemediationDispatcher>();

        act.Should().NotThrow(
            "the singleton dispatcher must not hold a root-bound IMediator; it must dispatch inside a created scope");
    }

    [Fact]
    public void MagenticChangeProposalRouter_ResolvesFromRoot_UnderScopeValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Mock.Of<IMediator>());

        // Mirrors the production lifetime (DependencyInjection.Magentic.cs —
        // RegisterMagenticServices is private, so the registration line is
        // replicated verbatim here).
        services.AddSingleton<MagenticChangeProposalRouter>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        var act = () => provider.GetRequiredService<MagenticChangeProposalRouter>();

        act.Should().NotThrow(
            "the singleton router must not hold a root-bound IMediator; it must dispatch inside a created scope");
    }
}
