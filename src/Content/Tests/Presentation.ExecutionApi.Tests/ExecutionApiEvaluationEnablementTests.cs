using Application.AI.Common.Evaluation;
using Application.AI.Common.Evaluation.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Tests for opting the evaluation framework into this host.
/// </summary>
/// <remarks>
/// Evaluation reads dataset files named by the caller. With no roots configured that is unconfined —
/// correct for the EvalRunner CLI on a developer's own machine, and an arbitrary-file-read probe on a
/// host anyone else can reach. These pin that the host refuses to start in that state rather than
/// booting into it.
/// </remarks>
public sealed class ExecutionApiEvaluationEnablementTests
{
    private static WebApplicationFactory<Program> BootWith(Dictionary<string, string?> variables)
    {
        foreach (var (key, value) in variables)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            var factory = new WebApplicationFactory<Program>();
            _ = factory.Server; // Force startup while the overrides are visible.
            return factory;
        }
        finally
        {
            foreach (var key in variables.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void EvaluationDisabled_LeavesTheFailFastRunnerInPlace()
    {
        // The shipped default. The framework's cost is not paid, and any dispatch fails loudly rather
        // than appearing to work.
        using var factory = new WebApplicationFactory<Program>();

        factory.Services.GetRequiredService<IEvalRunner>()
            .Should().BeOfType<NotConfiguredEvalRunner>();
    }

    [Fact]
    public void EvaluationEnabledWithoutDatasetRoots_RefusesToStart()
    {
        // The failure this whole design exists to prevent: a host that looks configured for evaluation
        // and reads whatever path a caller sends it.
        var act = () => BootWith(new Dictionary<string, string?>
        {
            ["AppConfig__AI__Evaluation__Enabled"] = "true",
        });

        act.Should().Throw<Exception>()
            .Which.Should().Match<Exception>(ex =>
                ex.Message.Contains("DatasetRoots", StringComparison.Ordinal)
                || (ex.InnerException != null
                    && ex.InnerException.Message.Contains("DatasetRoots", StringComparison.Ordinal)));
    }

    [Fact]
    public void EvaluationEnabledWithARoot_WiresTheRealRunner()
    {
        using var factory = BootWith(new Dictionary<string, string?>
        {
            ["AppConfig__AI__Evaluation__Enabled"] = "true",
            ["AppConfig__AI__Evaluation__DatasetRoots__0"] = Path.GetTempPath(),
        });

        factory.Services.GetRequiredService<IEvalRunner>()
            .Should().NotBeOfType<NotConfiguredEvalRunner>(
                "enabling evaluation must replace the fail-fast default, or the feature is inert");
    }

    [Fact]
    public void EvaluationEnabledWithOnlyBlankRoots_RefusesToStart()
    {
        // A whitespace entry binds as a root but confines nothing — it must not satisfy the
        // requirement, or the fail-closed check becomes a formality anyone can tick.
        var act = () => BootWith(new Dictionary<string, string?>
        {
            ["AppConfig__AI__Evaluation__Enabled"] = "true",
            ["AppConfig__AI__Evaluation__DatasetRoots__0"] = "   ",
        });

        act.Should().Throw<Exception>();
    }
}
