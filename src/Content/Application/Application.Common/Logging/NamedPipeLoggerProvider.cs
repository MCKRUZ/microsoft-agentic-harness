using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that streams log entries to a named pipe server
/// for real-time consumption by external viewers. The server auto-reconnects when
/// clients disconnect and drops messages non-blocking when the queue is full.
/// </summary>
/// <remarks>
/// Connect from another terminal with:
/// <code>
/// # Windows (PowerShell)
/// Get-Content \\.\pipe\agentic-harness-logs -Wait
///
/// # Linux/macOS
/// cat /tmp/agentic-harness-logs
/// </code>
/// The pipe name is configurable via <c>LoggingConfig.PipeName</c>.
/// </remarks>
public sealed class NamedPipeLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, NamedPipeLogger> _loggers = new();
    private readonly BlockingCollection<string> _messageQueue = new(500);
    private readonly IOptionsMonitor<LoggingConfig> _config;
    private readonly IExternalScopeProvider? _scopeProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _backgroundThread;

    /// <summary>
    /// Initializes a new instance of the <see cref="NamedPipeLoggerProvider"/> class.
    /// Starts the background pipe server thread immediately.
    /// </summary>
    /// <param name="config">Application configuration for pipe name resolution.</param>
    /// <param name="scopeProvider">Optional scope provider for agent context propagation.</param>
    public NamedPipeLoggerProvider(IOptionsMonitor<LoggingConfig> config, IExternalScopeProvider? scopeProvider = null)
    {
        _config = config;
        _scopeProvider = scopeProvider;
        _backgroundThread = new Thread(ProcessMessageQueue)
        {
            IsBackground = true,
            Name = "NamedPipeLogger"
        };
        _backgroundThread.Start();
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new NamedPipeLogger(name, this, _scopeProvider));

    /// <summary>
    /// Enqueues a message for streaming to connected pipe clients.
    /// Non-blocking; drops the message if the queue is full.
    /// </summary>
    /// <param name="message">The formatted log message to stream.</param>
    internal void WriteMessage(string message) =>
        _messageQueue.TryAdd(message);

    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private void ProcessMessageQueue()
    {
        var pipeName = _config.CurrentValue.PipeName;

        while (!_cts.Token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            StreamWriter? writer = null;
            string? pendingMessage = null;

            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                pipe.WaitForConnectionAsync(_cts.Token).Wait(_cts.Token);
                writer = new StreamWriter(pipe, PipeEncoding)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                while (!_cts.Token.IsCancellationRequested)
                {
                    var message = _messageQueue.Take(_cts.Token);
                    pendingMessage = message;

                    try
                    {
                        writer.WriteLine(message);
                        pendingMessage = null;
                    }
                    catch (IOException)
                    {
                        // Broken pipe: preserve the unsent message so the next client sees it.
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Pipe closed mid-write.
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Client disconnect during connect/setup; loop back.
            }
            catch (Exception)
            {
                // Swallow to keep the thread alive; cannot log from the logger without recursion.
            }
            finally
            {
                // StreamWriter.Dispose flushes and can throw on a broken pipe — guard it.
                try { writer?.Dispose(); } catch { }
                try { pipe?.Dispose(); } catch { }

                // Requeue any message that was dequeued but never made it onto the wire.
                if (pendingMessage is not null && !_cts.Token.IsCancellationRequested)
                    _messageQueue.TryAdd(pendingMessage);
            }
        }
    }

    private bool _disposed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _backgroundThread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
        _messageQueue.Dispose();
    }
}
