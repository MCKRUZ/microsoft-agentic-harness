using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Application.AI.Common.Services.Tools;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Iac;
using Infrastructure.AI.Tools.Iac;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Iac;

/// <summary>
/// Regression test for #425: a custom <see cref="ISandboxExecutor"/> — a template extensibility
/// seam — that violates its own non-nullable <c>ExecuteAsync</c> contract by returning
/// <see langword="null"/> used to flow straight into
/// <c>Result&lt;SandboxExecutionResult&gt;.Success(null)</c>, a confidently "successful" result
/// masking a contract violation until some later, unrelated caller dereferenced <c>.Value!</c>.
/// <see cref="IacSandboxRunner.RunAsync"/> now rejects a null executor result explicitly, turning it
/// into the same stable <see cref="Domain.Common.Result{T}.Fail"/> outcome every other dispatch-time
/// fault in that method already produces.
/// </summary>
public sealed class IacSandboxRunnerNullExecutorResultTests
{
    private const string ModuleDir = "/tmp/iac/module";

    private sealed class NullReturningSandboxExecutor : ISandboxExecutor
    {
        public Task<SandboxExecutionResult> ExecuteAsync(SandboxExecutionRequest request, CancellationToken ct)
            => Task.FromResult<SandboxExecutionResult>(null!);
    }

    private static IServiceScopeFactory ScopeFactory(ISandboxExecutor sandbox)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(SandboxIsolationLevel.Process, sandbox);
        services.AddKeyedSingleton(SandboxIsolationLevel.Container, sandbox);
        services.AddOptions<SandboxConfig>();
        services.AddSingleton(sp => new FirstPartyToolLookup(sp, new HashSet<string>()));
        services.AddSingleton<ToolPermissionProfileResolver>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task RunAsync_ExecutorReturnsNull_ReturnsFailInsteadOfSuccessWithNullValue()
    {
        var result = await IacSandboxRunner.RunAsync(
            program: "terraform",
            arguments: ["init"],
            moduleDirectory: ModuleDir,
            registryAllowlist: [],
            scopeFactory: ScopeFactory(new NullReturningSandboxExecutor()),
            defaultIsolationLevel: SandboxIsolationLevel.Process,
            toolName: "terraform_plan",
            requiredCapabilities: IacPlanTool.RequiredSandboxCapabilities,
            logger: NullLogger.Instance,
            backendLabel: "Terraform");

        // Old behavior: this was IsSuccess = true with Value = null — a confidently "successful"
        // Result masking the contract violation. RunAsync's catch-all deliberately reports only the
        // exception type name (never ex.Message, which can embed internal detail) — matching every
        // other dispatch-time fault this method already returns via Result.Fail.
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains(nameof(InvalidOperationException)));
    }
}
