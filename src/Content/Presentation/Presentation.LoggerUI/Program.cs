namespace Presentation.LoggerUI;

/// <summary>
/// Entry point for the log viewer console application.
/// Connects to one or more named pipe log servers and displays
/// colored, formatted output with Spectre.Console.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var options = LoggerOptions.FromArgs(args);

        var title = options.PipeNames.Count == 1
            ? $"Log Viewer - {options.PipeNames[0]}"
            : $"Log Viewer - {options.PipeNames.Count} sources";

        try { Console.Title = title; } catch { }
        try { Console.Clear(); } catch { }
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var cts = new CancellationTokenSource();
        var keyListenerTask = Task.Run(() => PipeLogReader.KeyboardListener(options.PipeNames, cts));

        LogEntryFormatter.PrintHeader(options.PipeNames);

        var readerTasks = options.PipeNames
            .Select((name, idx) => PipeLogReader.RunAsync(
                name, LogEntryFormatter.BuildSourceTag(name, idx), options.ParseJson, cts))
            .ToArray();

        await Task.WhenAll(readerTasks);

        cts.Cancel();
        try { await keyListenerTask; }
        catch { }
    }
}
