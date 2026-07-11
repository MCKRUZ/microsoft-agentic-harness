using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Presentation.BundleApi.Streaming;

/// <summary>
/// Writes <see cref="BundleStreamEvent"/>s to a response stream as Server-Sent-Events frames
/// (<c>data: {json}\n\n</c>, camelCase), flushing each so the client receives it immediately.
/// </summary>
/// <remarks>
/// Events are serialized against the <see cref="BundleStreamEvent"/> base type so
/// <see cref="JsonPolymorphicAttribute"/> emits the <c>type</c> discriminator on every frame — serializing by
/// runtime type would silently drop it. Writes are serialized behind a lock: even though a bundle run streams
/// from a single async flow, keeping frame writes atomic guards against any future concurrent producer
/// interleaving bytes into an unparseable frame (Kestrel also forbids concurrent response writes).
/// </remarks>
public sealed class BundleStreamEventWriter : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Initializes a writer targeting <paramref name="responseBody"/> (typically the HTTP response body).</summary>
    public BundleStreamEventWriter(Stream responseBody)
    {
        ArgumentNullException.ThrowIfNull(responseBody);
        _stream = responseBody;
    }

    /// <summary>Serializes <paramref name="evt"/> as one SSE frame and flushes it to the client.</summary>
    public async Task WriteAsync(BundleStreamEvent evt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var json = JsonSerializer.Serialize(evt, typeof(BundleStreamEvent), SerializerOptions);
        var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Releases the write-serialization semaphore. The response stream is owned by the host.</summary>
    public void Dispose() => _writeLock.Dispose();
}
