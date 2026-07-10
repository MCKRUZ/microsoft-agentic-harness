using System.Globalization;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Application.Core.CQRS.Evaluation.RunEvalSuite;
using Domain.AI.Evaluation;
using Infrastructure.AI.Evaluation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Presentation.Common.Extensions;

namespace Presentation.EvalRunner;

/// <summary>
/// Entry point for the offline evaluation CLI.
/// </summary>
/// <remarks>
/// <para><b>Usage:</b></para>
/// <code>
/// evalrun &lt;dataset.yaml&gt; [&lt;dataset2.yaml&gt; ...]
///         [--out FORMAT[:PATH]]        # output sink; repeatable; FORMAT must match a registered reporter
///         [--ingest-url URL]           # POST EvalRunReport to a dashboard ingest endpoint after the run; non-fatal on failure
///         [--repeats N]                # 1..50 (default 1; CI default 3)
///         [--parallel N]               # cases run concurrently (default 1)
///         [--tags tag1,tag2]           # only run cases with at least one matching tag
///         [--fail-rate F]              # 0.0..1.0 acceptable failure fraction (default 0)
///         [--deterministic]            # force temperature=0 on every invocation
/// </code>
/// <para>
/// <b>--out form:</b>
/// <list type="bullet">
///   <item><description><c>--out console</c> writes to stdout.</description></item>
///   <item><description><c>--out json:report.json</c> writes to <c>report.json</c>.</description></item>
///   <item><description>Multiple <c>--out</c> flags allowed; at most one may omit <c>:PATH</c> (the stdout one).</description></item>
///   <item><description>No <c>--out</c> at all defaults to <c>--out console</c>.</description></item>
/// </list>
/// </para>
/// <para>
/// Exit code 0 when the overall verdict is <see cref="Verdict.Pass"/>, 1 otherwise.
/// Validation failures (bad args, missing files) exit 2.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        // Subcommand: harmonic memory write-side eval (a different eval shape — facts + gold topics, not
        // agent Q&A — so it has its own runner rather than the dataset/IAgentInvoker pipeline below).
        if (args[0] == "harmonic-write")
        {
            return await HarmonicWriteEval.HarmonicWriteEvalCli.RunAsync(args[1..]);
        }

        CliArgs parsed;
        try
        {
            parsed = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Invalid arguments: {ex.Message}");
            PrintUsage();
            return 2;
        }

        if (parsed.DatasetPaths.Count == 0)
        {
            Console.Error.WriteLine("At least one dataset path is required.");
            PrintUsage();
            return 2;
        }

        // Cost/usage warnings are collected by RunEvalSuiteCommandHandler and surfaced
        // via EvalRunReport.Warnings — printed below after the run completes. The CLI
        // does not emit a duplicate pre-run warning; the report-attached warnings reach
        // every dispatcher (CLI, dashboard, scheduled job, REST endpoint) uniformly
        // without depending on logger filter routing.

        // Wire up SIGINT / Ctrl+C → cancellation across the whole run so a stuck judge
        // or long-running case can be aborted cleanly without truncating output files.
        // The handler is guarded against ObjectDisposedException because Console.CancelKeyPress
        // is a process-global static event and may fire during shutdown after the cts is
        // disposed; we also explicitly detach the handler in finally to avoid the race.
        var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            try
            {
                eventArgs.Cancel = true; // suppress immediate process exit; let the runner clean up
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // cts was already disposed (normal-exit race) — nothing to cancel.
            }
        };
        Console.CancelKeyPress += cancelHandler;

        var services = new ServiceCollection();
        services.GetServices(includeHealthChecksUI: false);
        services.AddEvaluationDependencies();

        // BuildValidatedServiceProvider enables ValidateScopes + ValidateOnBuild (audit H2).
        // The real IEvalRunner registered by AddEvaluationDependencies above wins over the
        // composition root's not-configured default via last-registration-wins.
        await using var provider = services.BuildValidatedServiceProvider();

        // Validate --out formats against actually-registered reporter format keys (not a
        // hardcoded list) so consumers who register custom IEvalReporter implementations
        // have their FormatKey accepted by the CLI automatically.
        var reportersByFormat = provider.GetServices<IEvalReporter>()
            .ToDictionary(r => r.FormatKey, StringComparer.OrdinalIgnoreCase);
        foreach (var sink in parsed.OutputSinks)
        {
            if (!reportersByFormat.ContainsKey(sink.Format))
            {
                Console.Error.WriteLine(
                    $"Unknown --out format '{sink.Format}'. Registered: {string.Join(", ", reportersByFormat.Keys.OrderBy(s => s))}.");
                return 2;
            }
        }

        // Hosted services tracked for orderly shutdown. We keep a SEPARATE list of services
        // that we successfully started so a mid-startup failure on service N doesn't leak
        // services 1..N-1 (and so we never call StopAsync on a service that never started).
        var allHostedServices = provider.GetServices<IHostedService>().ToList();
        var startedHostedServices = new List<IHostedService>(allHostedServices.Count);

        // Bound the shutdown wait so a hostile/hung hosted service can't pin the process
        // forever during SIGINT recovery or normal teardown.
        var shutdownTimeout = TimeSpan.FromSeconds(15);

        try
        {
            // Start hosted services (skill seeding, planner DB migration, governance bootstrap,
            // drift baseline loader, etc.) — the harness expects these to run before any
            // ExecuteAgentTurnCommand is dispatched. ConsoleUI/Program.cs follows the same pattern.
            foreach (var hostedService in allHostedServices)
            {
                await hostedService.StartAsync(cts.Token).ConfigureAwait(false);
                startedHostedServices.Add(hostedService);
            }

            var mediator = provider.GetRequiredService<IMediator>();

            var command = new RunEvalSuiteCommand
            {
                DatasetPaths = parsed.DatasetPaths,
                Options = new EvalRunOptions
                {
                    Repeats = parsed.Repeats,
                    Parallelism = parsed.Parallelism,
                    TagFilter = parsed.Tags,
                    FailRateThreshold = parsed.FailRateThreshold,
                    ForceDeterministic = parsed.Deterministic
                }
            };

            var result = await mediator.Send(command, cts.Token);
            if (!result.IsSuccess)
            {
                Console.Error.WriteLine("Eval run failed:");
                foreach (var err in result.Errors) Console.Error.WriteLine($"  - {err}");
                return 2;
            }

            var report = result.Value!;

            // Surface handler-attached advisories (cost warnings, etc.) on stderr so the
            // user sees them regardless of logging-pipeline filter config. These are the
            // canonical surface for non-fatal warnings — see EvalRunReport.Warnings.
            foreach (var warning in report.Warnings)
            {
                Console.Error.WriteLine($"Warning: {warning}");
            }

            // Single run, multiple emits — no double LLM cost.
            foreach (var sink in parsed.OutputSinks)
            {
                var reporter = reportersByFormat[sink.Format];
                if (sink.Path is { } outFile)
                {
                    var dir = Path.GetDirectoryName(outFile);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    if (File.Exists(outFile))
                    {
                        Console.Error.WriteLine($"Overwriting existing file: {outFile}");
                    }

                    await using var fs = File.Create(outFile);
                    await reporter.WriteAsync(report, fs, cts.Token);
                }
                else
                {
                    await using var stdout = Console.OpenStandardOutput();
                    await reporter.WriteAsync(report, stdout, cts.Token);
                }
            }

            // Optional dashboard ingest. Best-effort: a network blip should not turn a
            // green run red. PostReportAsync writes its own failure messages to stderr;
            // we don't propagate them into the exit code.
            if (parsed.IngestUrl is { } ingestUrl)
            {
                var bearer = Environment.GetEnvironmentVariable("EVAL_INGEST_TOKEN");
                var ingested = await EvalRunIngestClient.PostReportAsync(
                    ingestUrl,
                    report,
                    bearer,
                    TimeSpan.FromSeconds(30),
                    cts.Token).ConfigureAwait(false);
                if (ingested)
                {
                    Console.Error.WriteLine($"Ingested run {report.RunId} to {ingestUrl}.");
                }
            }

            return report.OverallVerdict == Verdict.Pass ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Eval run cancelled.");
            return 130; // conventional exit code for SIGINT
        }
        finally
        {
            // Stop only services we actually started, with a bounded timeout so a misbehaving
            // hosted service can't keep the process alive past the shutdown budget.
            using var stopCts = new CancellationTokenSource(shutdownTimeout);
            foreach (var hostedService in startedHostedServices)
            {
                try { await hostedService.StopAsync(stopCts.Token).ConfigureAwait(false); }
                catch { /* best-effort; we're shutting down */ }
            }

            // Detach the SIGINT handler BEFORE disposing the cts so the closure can no longer
            // observe (and dereference) a disposed cts.
            Console.CancelKeyPress -= cancelHandler;
            cts.Dispose();
        }
    }

    private static CliArgs ParseArgs(string[] args)
    {
        var datasets = new List<string>();
        var sinks = new List<OutputSink>();
        int repeats = 1;
        int parallelism = 1;
        double failRate = 0.0;
        bool deterministic = false;
        IReadOnlyList<string>? tags = null;
        string? ingestUrlRaw = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--out":
                    sinks.Add(ParseSink(RequireValue(args, ref i, "--out")));
                    break;
                case "--repeats":
                    repeats = ParsePositiveInt(RequireValue(args, ref i, "--repeats"), 1, 50, "--repeats");
                    break;
                case "--parallel":
                    parallelism = ParsePositiveInt(RequireValue(args, ref i, "--parallel"), 1, 128, "--parallel");
                    break;
                case "--fail-rate":
                    failRate = ParseFraction(RequireValue(args, ref i, "--fail-rate"));
                    break;
                case "--deterministic":
                    deterministic = true;
                    break;
                case "--tags":
                    tags = RequireValue(args, ref i, "--tags")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--ingest-url":
                    ingestUrlRaw = RequireValue(args, ref i, "--ingest-url");
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown flag '{a}'.");
                    datasets.Add(a);
                    break;
            }
        }

        if (sinks.Count == 0)
        {
            sinks.Add(new OutputSink("console", null));
        }

        // At most one --out can omit :PATH (the stdout sink) — multiple stdout writers
        // would interleave incoherently.
        var stdoutSinks = sinks.Count(s => s.Path is null);
        if (stdoutSinks > 1)
        {
            throw new ArgumentException(
                "At most one --out may omit :PATH (the stdout sink). Specify file paths for the others.");
        }

        // Same-target collision detection. Two distinct concerns:
        //   1. EXACT-PATH collision (any format) — two sinks open File.Create on the same
        //      file; the second silently clobbers the first. Reject regardless of format.
        //   2. FORMAT+PATH duplicate — purely redundant; reject.
        // Path comparison is OS-aware: Windows AND macOS default filesystems are
        // case-insensitive (NTFS, APFS, HFS+); Linux (ext4/xfs/btrfs) is case-sensitive.
        // Path.GetFullPath normalizes relative segments + separators.
        var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var resolvedPaths = sinks
            .Where(s => s.Path is not null)
            .Select(s => Path.GetFullPath(s.Path!))
            .ToList();

        var pathCollisions = resolvedPaths
            .GroupBy(p => p, pathComparer)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (pathCollisions.Count > 0)
        {
            throw new ArgumentException(
                $"Multiple --out sinks target the same file (would silently overwrite each other): " +
                $"{string.Join(", ", pathCollisions)}.");
        }

        // Duplicate (format, path) pairs are caught implicitly by the path-collision check
        // above for file sinks. The remaining case is duplicate stdout-format sinks
        // (e.g. --out console --out console), guarded by the stdoutSinks > 1 check earlier
        // when paired with non-stdout sinks, but identical-format stdout-only is still a
        // user error worth surfacing explicitly.
        var stdoutFormats = sinks
            .Where(s => s.Path is null)
            .GroupBy(s => s.Format, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (stdoutFormats.Count > 0)
        {
            throw new ArgumentException(
                $"Duplicate stdout --out sinks: {string.Join(", ", stdoutFormats)}.");
        }

        // Resolve ingest URL: CLI flag wins; otherwise EVAL_INGEST_URL env var. Both opt-in;
        // when neither is set the report is emitted to reporters only and the dashboard
        // sees nothing. Validate parseability early so a typo doesn't surface only at HTTP time.
        var ingestRaw = ingestUrlRaw ?? Environment.GetEnvironmentVariable("EVAL_INGEST_URL");
        Uri? ingestUrl = null;
        if (!string.IsNullOrWhiteSpace(ingestRaw))
        {
            if (!Uri.TryCreate(ingestRaw, UriKind.Absolute, out ingestUrl)
                || (ingestUrl.Scheme != Uri.UriSchemeHttp && ingestUrl.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException(
                    $"--ingest-url / EVAL_INGEST_URL must be an absolute http(s) URL. Got: '{ingestRaw}'.");
            }
        }

        return new CliArgs(datasets, sinks, repeats, parallelism, failRate, deterministic, tags, ingestUrl);
    }

    private static OutputSink ParseSink(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("'--out' requires a non-empty FORMAT[:PATH] value.");

        var colon = raw.IndexOf(':');
        if (colon < 0)
        {
            return new OutputSink(raw.Trim(), null);
        }

        var format = raw[..colon].Trim();
        var path = raw[(colon + 1)..].Trim();
        if (format.Length == 0)
            throw new ArgumentException($"'--out' FORMAT is empty in '{raw}'.");
        if (path.Length == 0)
            throw new ArgumentException($"'--out' PATH is empty in '{raw}' — drop the colon to write to stdout.");

        return new OutputSink(format, path);
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Flag '{flag}' requires a value.");
        return args[++i];
    }

    private static int ParsePositiveInt(string raw, int min, int max, string flag)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
            throw new ArgumentException($"'{flag}' must be an integer in [{min}, {max}], got '{raw}'.");
        return v;
    }

    private static double ParseFraction(string raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || v > 1)
            throw new ArgumentException($"'--fail-rate' must be in [0.0, 1.0], got '{raw}'.");
        return v;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: evalrun <dataset.yaml> [...] [options]

            Options:
              --out FORMAT[:PATH]   output sink; repeatable. FORMAT must match a
                                    registered IEvalReporter (default: console, json, junit).
                                    Omit :PATH to write to stdout (at most one such sink).
                                    Examples:
                                      --out console
                                      --out json:report.json
                                      --out junit:eval.xml --out json:eval.json
              --repeats N           1..50 (default 1; CI default 3); warns above 10 due to cost
              --parallel N          concurrent cases (default 1)
              --tags tag1,tag2      only run cases matching at least one tag
              --fail-rate F         max failed-case fraction for overall Pass (default 0.0)
              --deterministic       force temperature=0 on every invocation
              --ingest-url URL      POST the run report to a dashboard ingest endpoint
                                    after the reporters emit. Bearer token via
                                    EVAL_INGEST_TOKEN env var. Non-fatal on failure —
                                    a network error never changes the exit code.
                                    Alternative: set EVAL_INGEST_URL env var instead.

            Exit codes:
              0   — overall verdict Pass
              1   — overall verdict Fail / Warn
              2   — argument or load error
              130 — cancelled (SIGINT / Ctrl+C)
            """);
    }

    private sealed record CliArgs(
        IReadOnlyList<string> DatasetPaths,
        IReadOnlyList<OutputSink> OutputSinks,
        int Repeats,
        int Parallelism,
        double FailRateThreshold,
        bool Deterministic,
        IReadOnlyList<string>? Tags,
        Uri? IngestUrl);

    private sealed record OutputSink(string Format, string? Path);
}
