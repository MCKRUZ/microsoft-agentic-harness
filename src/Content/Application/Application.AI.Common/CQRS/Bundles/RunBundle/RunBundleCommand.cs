using Domain.AI.Bundles;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Bundles.RunBundle;

/// <summary>
/// Starts an asynchronous run of a previously staged bundle: creates a queued run record and hands it to the
/// background dispatcher, returning a job id the caller polls for the result. The application-level entry
/// point for <c>POST /api/bundles/{handle}/runs</c>. Async-job is the contract — this returns immediately,
/// it does not block on the conversation.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Envelope"/> is the <em>resolved</em> per-caller capability grant, not the bundle's own
/// requests. Resolving it from the caller's credential is the transport layer's job (the API middleware);
/// this command takes the already-resolved grant so the background dispatcher can re-publish it ambiently
/// for the whole run — the request-scoped ambient envelope does not flow onto the dispatcher's thread.
/// </para>
/// <para>
/// Resolving the envelope per run (rather than binding it at registration) means even a leaked handle runs
/// only under the <em>invoking</em> caller's grant, never the registrant's — defence in depth.
/// </para>
/// </remarks>
public sealed record RunBundleCommand : IRequest<Result<RunBundleResult>>
{
    /// <summary>The handle of the staged bundle to run.</summary>
    public required string Handle { get; init; }

    /// <summary>The user messages seeding the conversation. One turn per message, bounded by <see cref="MaxTurns"/>.</summary>
    public required IReadOnlyList<string> UserMessages { get; init; }

    /// <summary>The resolved per-caller capability grant this run executes under.</summary>
    public required CapabilityEnvelope Envelope { get; init; }

    /// <summary>The maximum number of turns the conversation may run.</summary>
    public int MaxTurns { get; init; } = 10;
}
