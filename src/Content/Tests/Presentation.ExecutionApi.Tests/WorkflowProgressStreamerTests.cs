using System.Text;
using System.Threading.Channels;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Runs;
using FluentAssertions;
using Presentation.ExecutionApi.Streaming;
using Xunit;

namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Drives <see cref="WorkflowProgressStreamer"/> directly, over the quiet path.
/// </summary>
/// <remarks>
/// <para>
/// The host-level tests all describe runs that produce events promptly, so they exercise the streamer
/// only while it is busy. A workflow step calling a model can easily run for minutes saying nothing,
/// and the code that carries a stream through that silence is the part least like the code the other
/// tests cover — it races a timer against a pending read, which is where this class can go wrong in
/// ways that finish fine when events keep arriving.
/// </para>
/// <para>
/// The keep-alive interval is injected so the silence is milliseconds rather than the production
/// quarter-minute. A test that waited out the real interval would be one nobody runs.
/// </para>
/// </remarks>
public sealed class WorkflowProgressStreamerTests
{
    /// <summary>A subscription the test feeds by hand, so silence is something it can choose.</summary>
    private sealed class ManualSubscription : IRunProgressSubscription
    {
        private readonly Channel<RunProgressEvent> _events =
            Channel.CreateUnbounded<RunProgressEvent>();

        public long DroppedCount => 0;

        public IAsyncEnumerable<RunProgressEvent> ReadAllAsync(CancellationToken cancellationToken) =>
            _events.Reader.ReadAllAsync(cancellationToken);

        public void Publish(RunProgressEvent evt) => _events.Writer.TryWrite(evt);

        public void Dispose() => _events.Writer.TryComplete();
    }

    /// <summary>A stream whose bytes the test can read while the streamer is still writing.</summary>
    private sealed class RecordingStream : Stream
    {
        private readonly Lock _gate = new();
        private readonly MemoryStream _written = new();

        public string Text
        {
            get
            {
                lock (_gate)
                    return Encoding.UTF8.GetString(_written.ToArray());
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
                _written.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _written.Write(buffer.Span);

            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written.Length;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static readonly TimeSpan ShortKeepAlive = TimeSpan.FromMilliseconds(100);

    /// <summary>A deadlock guard. A working stream returns as soon as the run finishes.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static RunRecord LiveRun() => new()
    {
        JobId = "job-1",
        Kind = RunKind.Workflow,
        TargetId = "workflow-1",
        OwnerId = "alice",
        TenantId = "acme",
        Envelope = new CapabilityEnvelope(),
        Status = RunStatus.Running,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private static RunProgressEvent Finished() => new()
    {
        JobId = "job-1",
        Sequence = 1,
        Kind = RunProgressKind.RunFinished,
        OccurredAt = DateTimeOffset.UnixEpoch,
        Status = nameof(RunStatus.Succeeded)
    };

    [Fact]
    public async Task AStreamThatGoesQuietForSeveralIntervals_StaysUsable()
    {
        // The bug this pins: the loop raced the pending read against a keep-alive timer and, when the
        // timer won, went round and started a *second* read on the same enumerator while the first was
        // still outstanding. Concurrent MoveNextAsync on one async enumerator is undefined, and here it
        // faulted on a thread-pool thread with nobody to observe it — taking the whole process down.
        // Every workflow step slower than the keep-alive interval reaches this path, so it is the
        // ordinary case for real work, not an edge one.
        using var subscription = new ManualSubscription();
        var body = new RecordingStream();
        var streamer = new WorkflowProgressStreamer(body, ShortKeepAlive);

        var streaming = streamer.StreamAsync(LiveRun(), subscription, CancellationToken.None);

        // Long enough for several keep-alives, so the loop must survive going round on the quiet path
        // repeatedly rather than merely once.
        await Task.Delay(TimeSpan.FromMilliseconds(550));

        streaming.IsFaulted.Should().BeFalse(
            "a quiet stream must not fault: {0}", streaming.Exception?.ToString() ?? "(none)");

        subscription.Publish(Finished());

        var completed = await Task.WhenAny(streaming, Task.Delay(Budget));
        completed.Should().BeSameAs(
            streaming, "the stream must still deliver events after going quiet, and close when the run ends");

        // Surfaces the real exception rather than letting the assertion below describe the symptom.
        await streaming;

        var text = body.Text;
        text.Should().Contain(": keep-alive", "a stream that goes quiet must keep saying so");
        text.Should().Contain("FINISHED", "the event published after the silence must still arrive");
    }
}
