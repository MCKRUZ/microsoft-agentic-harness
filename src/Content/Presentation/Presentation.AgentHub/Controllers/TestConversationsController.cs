using Application.Core.CQRS.Agents.RunConversation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// Development-only endpoint for triggering real agent conversations programmatically.
/// Used by Playwright E2E tests to exercise the full pipeline (MediatR, observability,
/// metrics, SignalR) and then verify the Dashboard renders the resulting data.
/// </summary>
/// <remarks>
/// This controller is only registered in Development environments. It dispatches
/// <see cref="RunConversationCommand"/> through MediatR, so the entire production
/// pipeline fires — no synthetic data, no mocks.
/// </remarks>
[ApiController]
[Route("api/test")]
[AllowAnonymous]
public sealed class TestConversationsController : ControllerBase
{
	private readonly IMediator _mediator;
	private readonly ILogger<TestConversationsController> _logger;
	private readonly IHostEnvironment _environment;

	public TestConversationsController(
		IMediator mediator,
		ILogger<TestConversationsController> logger,
		IHostEnvironment environment)
	{
		_mediator = mediator;
		_logger = logger;
		_environment = environment;
	}

	/// <summary>
	/// Triggers a real agent conversation through the full MediatR pipeline.
	/// The agent executes, tools fire, metrics emit, sessions record — all real.
	/// Returns 404 outside Development environments.
	/// </summary>
	[HttpPost("conversations")]
	public async Task<IActionResult> RunTestConversation(
		[FromBody] TestConversationRequest request,
		CancellationToken cancellationToken)
	{
		if (!_environment.IsDevelopment())
			return NotFound();

		_logger.LogInformation(
			"E2E test conversation requested: agent={AgentName}, messages={MessageCount}",
			request.AgentName, request.Messages.Count);

		var command = new RunConversationCommand
		{
			AgentName = request.AgentName ?? "echo-test",
			UserMessages = request.Messages.Count > 0
				? request.Messages
				: ["Hello, this is an E2E test message. Please process this through the full pipeline."],
			MaxTurns = request.MaxTurns ?? 10,
			ConversationId = request.ConversationId ?? Guid.NewGuid().ToString(),
		};

		var result = await _mediator.Send(command, cancellationToken);

		if (!result.Success)
		{
			_logger.LogError("E2E test conversation failed: {Error}", result.Error);
			return StatusCode(500, new { error = result.Error });
		}

		_logger.LogInformation(
			"E2E test conversation completed: {TurnCount} turns, {ToolCount} tool invocations",
			result.Turns.Count, result.TotalToolInvocations);

		return Ok(new TestConversationResponse
		{
			Success = result.Success,
			ConversationId = command.ConversationId,
			TurnCount = result.Turns.Count,
			TotalToolInvocations = result.TotalToolInvocations,
			FinalResponse = result.FinalResponse,
			Turns = result.Turns.Select(t => new TestTurnResponse
			{
				TurnNumber = t.TurnNumber,
				UserMessage = t.UserMessage,
				AgentResponse = t.AgentResponse,
				ToolsInvoked = t.ToolsInvoked,
			}).ToList(),
		});
	}

	/// <summary>
	/// Health check for E2E test infrastructure — confirms the echo agent pipeline is available.
	/// </summary>
	[HttpGet("health")]
	public IActionResult HealthCheck()
	{
		if (!_environment.IsDevelopment())
			return NotFound();

		return Ok(new { status = "ready", agent = "echo-test" });
	}
}

/// <summary>Request body for triggering a test conversation.</summary>
public sealed record TestConversationRequest
{
	/// <summary>Agent name — defaults to "echo-test" if omitted.</summary>
	public string? AgentName { get; init; }

	/// <summary>User messages to send. Defaults to a standard test message if empty.</summary>
	public IReadOnlyList<string> Messages { get; init; } = [];

	/// <summary>Maximum turns before stopping. Defaults to 10.</summary>
	public int? MaxTurns { get; init; }

	/// <summary>Optional conversation ID for correlation. Generated if omitted.</summary>
	public string? ConversationId { get; init; }
}

/// <summary>Response from a test conversation run.</summary>
public sealed record TestConversationResponse
{
	public required bool Success { get; init; }
	public required string ConversationId { get; init; }
	public required int TurnCount { get; init; }
	public required int TotalToolInvocations { get; init; }
	public required string FinalResponse { get; init; }
	public required IReadOnlyList<TestTurnResponse> Turns { get; init; }
}

/// <summary>Summary of a single turn within a test conversation.</summary>
public sealed record TestTurnResponse
{
	public required int TurnNumber { get; init; }
	public required string UserMessage { get; init; }
	public required string AgentResponse { get; init; }
	public IReadOnlyList<string> ToolsInvoked { get; init; } = [];
}
