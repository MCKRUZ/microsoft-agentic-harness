using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Presentation.EvalRunner.Tests.Composition;

/// <summary>
/// Pins #1070's last gap: the EvalRunner suppresses console logging so its report owns standard output, and its file
/// logs are written only once a log run is started, which this host never does. So when a turn failed with an internal
/// error, the report said only "An internal error occurred" and the real cause, logged by the turn handler, went
/// nowhere an operator could read. Errors now reach standard error.
/// </summary>
/// <remarks>
/// Built on the same service graph the host composes, so the redacting logger factory that wraps every local sink is
/// the one these lines pass through.
/// </remarks>
public sealed class EvalRunnerErrorLoggingTests
{
    [Fact]
    public void ConsoleSuppressed_AnErrorAndItsExceptionReachTheWriter()
    {
        using var stderr = new StringWriter();
        using var provider = Build(new LoggingConfig { SuppressConsoleOutput = true }, stderr);

        Logger(provider).LogError(
            new InvalidOperationException("model returned malformed JSON"),
            "Agent {AgentName} turn {TurnNumber} failed", "default", 1);

        stderr.ToString().Should().Contain("Agent default turn 1 failed")
            .And.Contain("InvalidOperationException").And.Contain("model returned malformed JSON");
    }

    [Fact]
    public void ConsoleSuppressed_WarningsAndInformationStayOut()
    {
        // Warnings include one per tool a host cannot build, per agent build: on every case they would bury the errors.
        using var stderr = new StringWriter();
        using var provider = Build(new LoggingConfig { SuppressConsoleOutput = true }, stderr);

        Logger(provider).LogWarning("tool render_image is not part of this agent's tool set");
        Logger(provider).LogInformation("Executing turn 1 for agent default");

        stderr.ToString().Should().BeEmpty();
    }

    [Fact]
    public void ConsoleNotSuppressed_AddsNothing()
    {
        // With console logging on, errors already reach the console; a second sink would print each one twice.
        using var stderr = new StringWriter();
        using var provider = Build(new LoggingConfig { SuppressConsoleOutput = false }, stderr);

        Logger(provider).LogError("Agent default turn 1 failed");

        stderr.ToString().Should().BeEmpty();
    }

    [Fact]
    public void ConsoleSuppressed_AnErrorIsRedactedLikeEveryOtherLocalSink()
    {
        using var stderr = new StringWriter();
        using var provider = Build(new LoggingConfig { SuppressConsoleOutput = true }, stderr);

        Logger(provider).LogError(new InvalidOperationException("caller was jane.doe@example.com"), "Turn failed");

        stderr.ToString().Should().Contain("Turn failed").And.NotContain("jane.doe@example.com");
    }

    private static ServiceProvider Build(LoggingConfig logging, TextWriter stderr)
    {
        var services = EvalRunnerTestComposition.BuildServices();
        services.AddErrorsToStandardError(logging, stderr);
        return services.BuildServiceProvider();
    }

    private static ILogger Logger(IServiceProvider provider) =>
        provider.GetRequiredService<ILoggerFactory>().CreateLogger("Application.Core.CQRS.Agents.ExecuteAgentTurn");
}
