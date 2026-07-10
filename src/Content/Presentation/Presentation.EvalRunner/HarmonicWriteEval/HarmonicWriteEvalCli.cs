using Application.AI.Common.Evaluation.Models;
using Domain.Common.Config;
using Infrastructure.AI.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Presentation.Common.Extensions;

namespace Presentation.EvalRunner.HarmonicWriteEval;

/// <summary>
/// CLI entry for the harmonic memory write-side eval subcommand:
/// <c>evalrun harmonic-write &lt;fixture.json&gt; [--llm] [--out json:PATH]</c>.
/// </summary>
/// <remarks>
/// Offline by default (deterministic providers, no host, no cost). <c>--llm</c> builds the eval host to
/// resolve the chat-client factory + judge and runs the paid path — the only path that yields the real
/// abstraction-quality and clustering numbers.
/// </remarks>
public static class HarmonicWriteEvalCli
{
    /// <summary>Runs the subcommand. Returns 0 on success, 2 on argument/load error, 130 on cancellation.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        string? fixturePath = null;
        bool useLlm = false;
        string? jsonOutPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--llm":
                    useLlm = true;
                    break;
                case "--out":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--out requires a value, e.g. --out json:report.json");
                        return 2;
                    }
                    var sink = args[++i];
                    const string jsonPrefix = "json:";
                    if (!sink.StartsWith(jsonPrefix, StringComparison.OrdinalIgnoreCase) || sink.Length == jsonPrefix.Length)
                    {
                        Console.Error.WriteLine($"Unsupported --out '{sink}'. Only 'json:PATH' is supported (console is always printed).");
                        return 2;
                    }
                    jsonOutPath = sink[jsonPrefix.Length..];
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"Unknown flag '{a}'.");
                        PrintUsage();
                        return 2;
                    }
                    if (fixturePath is not null)
                    {
                        Console.Error.WriteLine("Only one fixture path may be given.");
                        return 2;
                    }
                    fixturePath = a;
                    break;
            }
        }

        if (fixturePath is null)
        {
            Console.Error.WriteLine("A fixture path is required.");
            PrintUsage();
            return 2;
        }

        var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; try { cts.Cancel(); } catch (ObjectDisposedException) { } };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            HarmonicWriteFixture fixture;
            try
            {
                fixture = await HarmonicWriteFixture.LoadAsync(fixturePath, cts.Token);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Failed to load fixture: {ex.Message}");
                return 2;
            }

            await using var provider = BuildProvider(useLlm);
            var timestamp = DateTime.UtcNow.ToString("O");

            var report = await HarmonicWriteEvalApp.RunAsync(provider, fixture, useLlm, timestamp, cts.Token);

            Console.WriteLine(report.ToConsole());

            if (jsonOutPath is not null)
            {
                var dir = Path.GetDirectoryName(jsonOutPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(jsonOutPath, report.ToJson(), cts.Token);
                Console.Error.WriteLine($"Wrote JSON report to {jsonOutPath}.");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Harmonic write-eval cancelled.");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            cts.Dispose();
        }
    }

    // Offline runs need no services (deterministic providers touch nothing). The paid path builds the eval
    // host so the chat-client factory + judge resolve; hosted services are intentionally not started — the
    // chat client and judge are constructed on demand and depend only on configuration.
    private static ServiceProvider BuildProvider(bool useLlm)
    {
        var services = new ServiceCollection();
        if (useLlm)
        {
            services.GetServices(includeHealthChecksUI: false);
            services.AddEvaluationDependencies();
            ConfigureJudgeFromAgentFramework(services);
        }
        // Same validation policy as the main EvalRunner host (audit H2). The offline path
        // composes nothing, so validation is a no-op there; the --llm path validates the
        // full eval-host graph, matching Program.cs.
        return services.BuildValidatedServiceProvider();
    }

    // The quality judge resolves its model from JudgeOptions — a DIFFERENT config section than the
    // abstractor/consolidator, which read AppConfig:AI:AgentFramework. AddEvaluationDependencies leaves
    // JudgeOptions.Deployment empty, so GetJudgeAsync throws "Deployment must be configured", DefaultLlmJudge
    // soft-fails every call, and the abstraction-quality column comes back blank on a paid run. Bind the judge
    // to the same model the eval already exercises so --llm actually scores abstraction quality.
    internal static void ConfigureJudgeFromAgentFramework(IServiceCollection services)
    {
        services.AddOptions<JudgeOptions>().Configure<IOptionsMonitor<AppConfig>>((judge, appConfig) =>
        {
            var framework = appConfig.CurrentValue.AI.AgentFramework;
            judge.ClientType = framework.ClientType;
            judge.Deployment = framework.DefaultDeployment;
        });
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: evalrun harmonic-write <fixture.json> [--llm] [--out json:PATH]

            Compares the harmonic memory write modes (Off / AbstractOnly / Full) on a fixed fact fixture,
            reporting fragmentation, cluster purity, LLM-call cost, and (with --llm) abstraction quality.

            Options:
              --llm            use the paid LLM providers + quality judge (default: offline deterministic)
              --out json:PATH  also write the JSON report to PATH (console is always printed)

            Note: this is a WRITE-side gate. It does not measure recall quality — the recall path does not
            consume abstractions until PR3, so a recall eval would show no delta across modes pre-PR3.
            """);
    }
}
