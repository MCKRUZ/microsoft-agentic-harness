using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Presentation.AgentHub.AgUi;

/// <summary>
/// Writes AG-UI events as Server-Sent Events frames to a response stream.
/// Each event is serialized as <c>data: {json}\n\n</c>.
/// </summary>
public interface IAgUiEventWriter
{
    /// <summary>
    /// Writes a single AG-UI event as an SSE data frame and flushes the stream.
    /// </summary>
    /// <param name="evt">The AG-UI event to serialize and send.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteAsync(AgUiEvent evt, CancellationToken ct = default);
}

/// <summary>
/// SSE event writer that serializes <see cref="AgUiEvent"/> records to the
/// AG-UI wire format: <c>data: {json}\n\n</c> with camelCase property names.
/// </summary>
/// <remarks>
/// <para>
/// Serialization uses <see cref="typeof(AgUiEvent)"/> as the declared type rather
/// than the runtime concrete type. This is required so that
/// <see cref="System.Text.Json.Serialization.JsonPolymorphicAttribute"/> emits the
/// <c>type</c> discriminator field on every frame. Serializing by runtime type
/// bypasses the polymorphic contract and silently drops the discriminator.
/// </para>
/// <para>
/// Each call flushes the underlying stream immediately so that HTTP chunked
/// transfer delivers frames to the client without buffering.
/// </para>
/// </remarks>
public sealed class AgUiEventWriter : IAgUiEventWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stream _stream;

    /// <summary>
    /// Initializes a new <see cref="AgUiEventWriter"/> that writes to <paramref name="responseBody"/>.
    /// </summary>
    /// <param name="responseBody">
    /// The response body stream. Typically <c>HttpContext.Response.Body</c>.
    /// </param>
    public AgUiEventWriter(Stream responseBody)
    {
        _stream = responseBody;
    }

    /// <inheritdoc />
    public async Task WriteAsync(AgUiEvent evt, CancellationToken ct = default)
    {
        // Serialize as the base AgUiEvent type so JsonPolymorphic emits the "type" discriminator.
        var json = JsonSerializer.Serialize(evt, typeof(AgUiEvent), SerializerOptions);
        var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await _stream.WriteAsync(frame, ct);
        await _stream.FlushAsync(ct);
    }
}
