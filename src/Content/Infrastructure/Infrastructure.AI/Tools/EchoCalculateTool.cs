using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Changes;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Deterministic calculation tool for E2E testing. Returns canned results so
/// the echo agent exercises multiple tool invocations in a single turn.
/// </summary>
public sealed class EchoCalculateTool : ITool
{
	public const string ToolName = "echo_calculate";

	private static readonly IReadOnlyList<string> Operations = ["calculate"];

	/// <inheritdoc />
	public string Name => ToolName;

	/// <inheritdoc />
	public string Description => "Performs a calculation on the given expression. Returns deterministic test data for E2E verification.";

	/// <inheritdoc />
	public IReadOnlyList<string> SupportedOperations => Operations;

	/// <inheritdoc />
	public bool IsReadOnly => true;

	/// <inheritdoc />
	public BlastRadius RiskTier => BlastRadius.Trivial;

	/// <inheritdoc />
	public bool IsConcurrencySafe => true;

	/// <inheritdoc />
	public Task<ToolResult> ExecuteAsync(
		string operation,
		IReadOnlyDictionary<string, object?> parameters,
		CancellationToken cancellationToken = default)
	{
		var expression = parameters.TryGetValue("expression", out var e) ? e?.ToString() ?? "0" : "0";

		var result = $"Calculation result for \"{expression}\": 42. " +
			"Computation completed in 12ms with 3 intermediate steps.";

		return Task.FromResult(ToolResult.Ok(result));
	}
}
