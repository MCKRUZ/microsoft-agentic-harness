using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Deterministic tool for E2E testing that simulates a knowledge lookup.
/// Returns canned results so the echo agent pipeline exercises tool invocation
/// metrics, observability recording, and dashboard rendering without external dependencies.
/// </summary>
public sealed class EchoLookupTool : ITool
{
	public const string ToolName = "echo_lookup";

	private static readonly IReadOnlyList<string> Operations = ["lookup"];

	/// <inheritdoc />
	public string Name => ToolName;

	/// <inheritdoc />
	public string Description => "Looks up information about a given topic. Returns deterministic test data for E2E verification.";

	/// <inheritdoc />
	public IReadOnlyList<string> SupportedOperations => Operations;

	/// <inheritdoc />
	public bool IsReadOnly => true;

	/// <inheritdoc />
	public bool IsConcurrencySafe => true;

	/// <inheritdoc />
	public Task<ToolResult> ExecuteAsync(
		string operation,
		IReadOnlyDictionary<string, object?> parameters,
		CancellationToken cancellationToken = default)
	{
		var topic = parameters.TryGetValue("topic", out var t) ? t?.ToString() ?? "unknown" : "unknown";

		var result = $"Echo lookup results for \"{topic}\": " +
			"Found 3 relevant documents with high confidence. " +
			"Document 1: Architecture overview (relevance: 0.95). " +
			"Document 2: Implementation guide (relevance: 0.87). " +
			"Document 3: Testing patterns (relevance: 0.82).";

		return Task.FromResult(ToolResult.Ok(result));
	}
}
