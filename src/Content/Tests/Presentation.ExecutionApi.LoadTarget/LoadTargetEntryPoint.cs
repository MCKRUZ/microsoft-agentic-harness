namespace Presentation.ExecutionApi.LoadTarget;

/// <summary>
/// Boots the Execution API as a load-test target and serves until stopped (Ctrl+C or process kill).
/// Normally launched by <c>scripts/load/Invoke-ExecutionApiLoad.ps1</c>, which also runs k6 against it.
/// </summary>
/// <remarks>
/// A named class rather than top-level statements on purpose. Top-level statements compile to a
/// global <c>Program</c> type, which would shadow the Execution API's own <c>Program</c> and make
/// <c>WebApplicationFactory&lt;Program&gt;</c> host this console app instead of the API.
/// </remarks>
public static class LoadTargetEntryPoint
{
    /// <summary>The line the runner waits for before starting k6; everything after it is informational.</summary>
    public const string ReadySignal = "LOAD TARGET READY";

    /// <summary>Runs the load target.</summary>
    /// <param name="args">See <see cref="LoadTargetOptions.Usage"/>.</param>
    /// <returns>0 after a clean stop, 2 for a bad command line.</returns>
    public static async Task<int> Main(string[] args)
    {
        var (options, error) = LoadTargetOptions.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(LoadTargetOptions.Usage);
            return 2;
        }

        using var issuer = new LocalTokenIssuer();
        var stateRoot = LoadTargetEnvironment.Apply(options, issuer);

        await using var host = new LoadTargetHost(options, issuer);
        host.Start();
        LoadTargetFixtures.Write(options, issuer, host.BaseUrl);

        Console.WriteLine($"{ReadySignal} {host.BaseUrl}");
        Console.WriteLine($"  model    {LoadTargetEnvironment.Describe(options.Model)}");
        Console.WriteLine($"  callers  {options.Callers} across {options.Tenants} tenants");
        Console.WriteLine($"  fixtures {options.OutputDirectory}");
        Console.WriteLine($"  state    {stateRoot}");

        await WaitForStopAsync();
        return 0;
    }

    private static async Task WaitForStopAsync()
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.Infinite, stop.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C: return so the host is disposed cleanly.
        }
    }
}
