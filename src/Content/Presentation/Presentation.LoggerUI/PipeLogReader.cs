using System.IO.Pipes;

namespace Presentation.LoggerUI;

/// <summary>
/// Manages named pipe connections and reads log lines from one or more pipe servers.
/// Each pipe reconnects independently on disconnect.
/// </summary>
internal static class PipeLogReader
{
    /// <summary>
    /// Reader loop for a single named pipe; reconnects on disconnect.
    /// </summary>
    internal static async Task RunAsync(string pipeName, string sourceTag, bool parseJson, CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.In);

                LogEntryFormatter.WriteStatusLine($"{sourceTag} [yellow]Waiting for log server ({pipeName})...[/]");
                await pipeClient.ConnectAsync(cts.Token);
                LogEntryFormatter.WriteStatusLine($"{sourceTag} [green]Connected![/]");

                using var reader = new StreamReader(pipeClient);

                string? line;
                while ((line = await reader.ReadLineAsync(cts.Token)) != null && !cts.IsCancellationRequested)
                {
                    LogEntryFormatter.ProcessLogLine(line, sourceTag, parseJson);
                }

                LogEntryFormatter.WriteStatusLine($"{sourceTag} [yellow]Server disconnected. Reconnecting...[/]");
            }
            catch (OperationCanceledException) { break; }
            catch (IOException)
            {
                LogEntryFormatter.WriteStatusLine($"{sourceTag} [yellow]Connection lost. Reconnecting...[/]");
            }
            catch (Exception ex)
            {
                LogEntryFormatter.WriteStatusLine($"{sourceTag} [red]Error: {ex.Message}[/]");
            }

            try { await Task.Delay(500, cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Listens for keyboard input (C to clear, Ctrl+C to exit).
    /// </summary>
    internal static void KeyboardListener(IReadOnlyList<string> pipeNames, CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(true); }
            catch { return; }
            if (key.Key == ConsoleKey.C)
            {
                Console.Clear();
                LogEntryFormatter.PrintHeader(pipeNames);
            }
            else if (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.C)
            {
                cts.Cancel();
                break;
            }
        }
    }
}
