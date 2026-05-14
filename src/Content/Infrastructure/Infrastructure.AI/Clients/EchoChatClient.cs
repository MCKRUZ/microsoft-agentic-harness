using Microsoft.Extensions.AI;

namespace Infrastructure.AI.Clients;

/// <summary>
/// Deterministic <see cref="IChatClient"/> for E2E testing that returns canned responses
/// with simulated tool calls and realistic token usage. Exercises the full agent pipeline
/// (MediatR, observability, metrics, SignalR) without requiring an external LLM endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Behaviour varies based on conversation state:
/// <list type="bullet">
///   <item>First call with tools available: returns a <see cref="FunctionCallContent"/> for "echo_lookup"</item>
///   <item>Subsequent call after tool result: returns a text summary referencing the tool output</item>
///   <item>Call without tools: returns a plain text response immediately</item>
/// </list>
/// </para>
/// <para>
/// Every response includes <see cref="UsageDetails"/> so the observability middleware
/// records realistic token counts, cache metrics, and cost estimates.
/// </para>
/// </remarks>
public sealed class EchoChatClient : IChatClient
{
	private const string ModelId = "echo-test-1.0";
	private const int SimulatedInputTokens = 150;
	private const int SimulatedOutputTokens = 75;
	private const int SimulatedCacheRead = 50;
	private const int SimulatedCacheWrite = 100;

	/// <inheritdoc />
	public ChatClientMetadata Metadata { get; } = new(nameof(EchoChatClient), null, ModelId);

	/// <inheritdoc />
	public Task<ChatResponse> GetResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		var messageList = messages.ToList();

		var hasFunctionResults = messageList
			.SelectMany(m => m.Contents)
			.Any(c => c is FunctionResultContent);

		var hasTools = options?.Tools?.Count > 0;

		if (!hasFunctionResults && hasTools)
		{
			return Task.FromResult(CreateToolCallResponse(options!));
		}

		return Task.FromResult(CreateTextResponse(messageList, hasFunctionResults));
	}

	/// <inheritdoc />
	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var response = await GetResponseAsync(messages, options, cancellationToken);

		foreach (var message in response.Messages)
		{
			foreach (var content in message.Contents)
			{
				yield return new ChatResponseUpdate
				{
					Role = message.Role,
					Contents = [content],
					ModelId = ModelId,
				};
			}
		}
	}

	/// <inheritdoc />
	public object? GetService(Type serviceType, object? key = null)
	{
		if (serviceType == typeof(ChatClientMetadata))
			return Metadata;

		return null;
	}

	/// <inheritdoc />
	public void Dispose() { }

	private static ChatResponse CreateToolCallResponse(ChatOptions options)
	{
		var toolName = options.Tools!
			.OfType<AIFunction>()
			.Select(t => t.Name)
			.FirstOrDefault() ?? "echo_lookup";

		var functionCall = new FunctionCallContent(
			callId: $"echo-call-{Guid.NewGuid():N}",
			name: toolName,
			arguments: new Dictionary<string, object?>
			{
				["topic"] = "E2E test verification data"
			});

		var assistantMessage = new ChatMessage(ChatRole.Assistant, [functionCall]);

		return new ChatResponse([assistantMessage])
		{
			ModelId = ModelId,
			Usage = CreateUsage(),
		};
	}

	private static ChatResponse CreateTextResponse(List<ChatMessage> messages, bool hadToolResults)
	{
		var userMessage = messages
			.LastOrDefault(m => m.Role == ChatRole.User)
			?.Text ?? "no input";

		var responseText = hadToolResults
			? $"Based on the tool results, here is a summary for your request: \"{userMessage}\". " +
			  "The echo agent successfully processed your request through the full pipeline, " +
			  "including tool invocation, observability recording, and metrics emission."
			: $"Echo response to: \"{userMessage}\". " +
			  "This is a deterministic response from the echo test agent.";

		var assistantMessage = new ChatMessage(ChatRole.Assistant, responseText);

		return new ChatResponse([assistantMessage])
		{
			ModelId = ModelId,
			Usage = CreateUsage(),
		};
	}

	private static UsageDetails CreateUsage() => new()
	{
		InputTokenCount = SimulatedInputTokens,
		OutputTokenCount = SimulatedOutputTokens,
		AdditionalCounts = new AdditionalPropertiesDictionary<long>
		{
			["CacheReadInputTokens"] = SimulatedCacheRead,
			["CacheCreationInputTokens"] = SimulatedCacheWrite,
		},
	};
}
