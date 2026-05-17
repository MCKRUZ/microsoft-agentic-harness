using System.Text.Json;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Planner;
using Domain.AI.Planner;
using Domain.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Planner;

/// <summary>
/// Converts a natural-language task description into a validated <see cref="PlanGraph"/> DAG
/// by sending structured generation requests to an LLM and mapping the JSON output to domain types.
/// </summary>
public sealed class LlmPlanGeneratorService : IPlanGenerator
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IPlanValidator _validator;
    private readonly IOptionsMonitor<PlannerOptions> _options;
    private readonly ILogger<LlmPlanGeneratorService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LlmPlanGeneratorService(
        IChatClientFactory chatClientFactory,
        IPlanValidator validator,
        IOptionsMonitor<PlannerOptions> options,
        ILogger<LlmPlanGeneratorService> logger)
    {
        _chatClientFactory = chatClientFactory;
        _validator = validator;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<PlanGraph>> GenerateAsync(
        string taskDescription,
        PlanGenerationConstraints? constraints = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(taskDescription);

        var opts = _options.CurrentValue;

        try
        {
            var chatClient = await _chatClientFactory.GetChatClientAsync(
                opts.ClientType,
                opts.GenerationModel,
                ct);

            var messages = BuildMessages(taskDescription, constraints);
            var chatOptions = new ChatOptions
            {
                Temperature = (float)opts.GenerationTemperature,
                MaxOutputTokens = opts.GenerationMaxTokens
            };

            var response = await chatClient.GetResponseAsync(messages, chatOptions, ct);
            var responseText = SanitizeJsonResponse(response.Text ?? string.Empty);

            if (string.IsNullOrWhiteSpace(responseText))
            {
                _logger.LogWarning("LLM returned empty response for plan generation");
                return Result<PlanGraph>.Fail("LLM returned an empty response.");
            }

            LlmPlanOutput? planOutput;
            try
            {
                planOutput = JsonSerializer.Deserialize<LlmPlanOutput>(responseText, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize LLM plan output as JSON");
                return Result<PlanGraph>.Fail($"LLM did not return valid JSON: {ex.Message}");
            }

            if (planOutput is null || planOutput.Steps.Count == 0)
                return Result<PlanGraph>.Fail("LLM returned an empty or null plan.");

            PlanGraph graph;
            try
            {
                graph = LlmPlanOutputMapper.MapToPlanGraph(planOutput);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to map LLM output to PlanGraph");
                return Result<PlanGraph>.Fail($"Failed to parse plan: {ex.Message}");
            }

            var validationResult = await _validator.ValidateAsync(graph, ct);
            if (!validationResult.IsSuccess)
                return Result<PlanGraph>.Fail(validationResult.Errors.ToArray());

            if (validationResult.Value is null)
                return Result<PlanGraph>.Fail("Validator returned null result.");

            if (!validationResult.Value.IsValid)
                return Result<PlanGraph>.Fail(validationResult.Value.Errors.ToArray());

            _logger.LogInformation(
                "Generated plan '{PlanName}' with {StepCount} steps and {EdgeCount} edges",
                graph.Name, graph.Steps.Count, graph.Edges.Count);

            return Result<PlanGraph>.Success(graph);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plan generation failed");
            return Result<PlanGraph>.Fail($"Plan generation failed: {ex.Message}");
        }
    }

    private static List<ChatMessage> BuildMessages(string taskDescription, PlanGenerationConstraints? constraints)
    {
        var systemPrompt = BuildSystemPrompt(constraints);
        var userPrompt = BuildUserPrompt(taskDescription, constraints);

        return
        [
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, userPrompt)
        ];
    }

    private static string BuildSystemPrompt(PlanGenerationConstraints? constraints)
    {
        var sb = new System.Text.StringBuilder(2048);
        sb.AppendLine("You are a plan generation engine. Given a task description, produce a valid execution plan as JSON.");
        sb.AppendLine();
        sb.AppendLine("Output format:");
        sb.AppendLine("""
            {
              "name": "string — human-readable plan name",
              "steps": [
                {
                  "name": "string — unique step name",
                  "type": "LlmCall | ToolUse | HumanGate | ConditionalBranch | SubPlanInvocation",
                  "configuration": { ... type-specific fields ... },
                  "retryPolicy": { "maxRetries": 3, "initialDelayMs": 1000, "strategy": "Exponential | Fixed | Linear", "onExhausted": "FailStep | SkipStep | FailPlan" },
                  "timeoutSeconds": 60
                }
              ],
              "edges": [
                { "from": "step-name-1", "to": "step-name-2", "type": "DataFlow | ControlFlow | ConditionalTrue | ConditionalFalse", "condition": "optional expression" }
              ],
              "configuration": { "planTimeoutMinutes": 30, "maxParallelSteps": 10, "maxSubPlanDepth": 5 }
            }
            """);
        sb.AppendLine();
        sb.AppendLine("Step configuration subtypes:");
        sb.AppendLine("- LlmCall: { \"type\": \"llm_call\", \"systemPrompt\": \"...\", \"modelDeploymentKey\": \"...\", \"temperature\": 0.7, \"maxTokens\": 4096 }");
        sb.AppendLine("- ToolUse: { \"type\": \"tool_use\", \"toolName\": \"...\", \"inputParameters\": { ... } }");
        sb.AppendLine("- HumanGate: { \"type\": \"human_gate\", \"description\": \"...\", \"timeoutMinutes\": 60 }");
        sb.AppendLine("- ConditionalBranch: { \"type\": \"conditional_branch\", \"conditionExpression\": \"...\", \"trueTarget\": \"step-name\", \"falseTarget\": \"step-name\" }");
        sb.AppendLine("- SubPlanInvocation: { \"type\": \"sub_plan\", \"isolateContext\": true }");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- The plan MUST be a valid DAG (no cycles).");
        sb.AppendLine("- Step names must be unique within the plan.");
        sb.AppendLine("- Edge 'from' and 'to' must reference existing step names.");
        sb.AppendLine("- Output ONLY valid JSON, no markdown fences or explanatory text.");

        if (constraints is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Constraints:");
            if (constraints.MaxSteps.HasValue)
                sb.AppendLine($"- Maximum steps: {constraints.MaxSteps.Value}");
            if (constraints.AllowedStepTypes is { Count: > 0 })
                sb.AppendLine($"- Allowed step types: {string.Join(", ", constraints.AllowedStepTypes)}");
            if (constraints.MaxSubPlanDepth.HasValue)
                sb.AppendLine($"- Maximum sub-plan depth: {constraints.MaxSubPlanDepth.Value}");
            if (constraints.MaxTotalTimeout.HasValue)
                sb.AppendLine($"- Maximum total timeout: {constraints.MaxTotalTimeout.Value.TotalMinutes} minutes");
        }

        return sb.ToString();
    }

    private static string BuildUserPrompt(string taskDescription, PlanGenerationConstraints? constraints)
    {
        if (!string.IsNullOrWhiteSpace(constraints?.AdditionalContext))
            return $"Task: {taskDescription}\n\nAdditional context: {constraints.AdditionalContext}";

        return $"Task: {taskDescription}";
    }

    /// <summary>
    /// Strips markdown code fences that LLMs frequently add despite instructions not to.
    /// </summary>
    private static string SanitizeJsonResponse(string response)
    {
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                return trimmed[(firstNewline + 1)..lastFence].Trim();
        }
        return trimmed;
    }
}
