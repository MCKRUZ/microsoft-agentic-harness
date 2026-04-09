using System.Collections.Concurrent;
using System.Threading.Channels;
using Application.AI.Common.Interfaces.Agents;
using Domain.AI.Agents;

namespace Infrastructure.AI.Agents;

/// <summary>
/// In-memory implementation of <see cref="IAgentMailbox"/> using
/// <see cref="Channel{T}"/> for lock-free, async-friendly message passing.
/// </summary>
/// <remarks>
/// Each agent gets an unbounded channel created on first access. Messages are
/// delivered in FIFO order. This implementation is suitable for single-process
/// scenarios; distributed deployments should use a persistent backing store.
/// </remarks>
public sealed class InMemoryAgentMailbox : IAgentMailbox
{
    private readonly ConcurrentDictionary<string, Channel<AgentMessage>> _channels = new();

    /// <inheritdoc />
    public async Task SendAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var channel = GetOrCreateChannel(message.ToAgentId);
        await channel.Writer.WriteAsync(message, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentMessage>> ReceiveAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var channel = GetOrCreateChannel(agentId);
        var messages = new List<AgentMessage>();

        while (channel.Reader.TryRead(out var message))
        {
            messages.Add(message);
        }

        return Task.FromResult<IReadOnlyList<AgentMessage>>(messages.AsReadOnly());
    }

    /// <inheritdoc />
    public async Task<AgentMessage?> WaitForResponseAsync(
        string agentId,
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var channel = GetOrCreateChannel(agentId);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, cancellationToken);

        var nonMatching = new List<AgentMessage>();

        try
        {
            while (await channel.Reader.WaitToReadAsync(linkedCts.Token))
            {
                while (channel.Reader.TryRead(out var message))
                {
                    if (string.Equals(message.CorrelationId, correlationId, StringComparison.Ordinal))
                    {
                        // Re-queue non-matching messages before returning
                        foreach (var requeue in nonMatching)
                        {
                            await channel.Writer.WriteAsync(requeue, cancellationToken);
                        }

                        return message;
                    }

                    nonMatching.Add(message);
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timeout elapsed — fall through to re-queue and return null
        }

        // Re-queue all non-matching messages
        foreach (var requeue in nonMatching)
        {
            await channel.Writer.WriteAsync(requeue, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Gets or creates an unbounded channel for the specified agent.
    /// </summary>
    private Channel<AgentMessage> GetOrCreateChannel(string agentId)
    {
        return _channels.GetOrAdd(agentId, _ =>
            Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            }));
    }
}
