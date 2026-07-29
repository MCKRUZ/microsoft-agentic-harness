namespace Presentation.ExecutionApi.Tests;

/// <summary>
/// Builders for workflow-submission request bodies, shared by the suites that drive
/// <c>POST /api/workflows</c> over HTTP.
/// </summary>
/// <remarks>
/// Anonymous objects rather than the Application-layer wire records, deliberately. These tests exist
/// to prove what a real caller sending real JSON receives, and constructing the request from the same
/// types the server deserializes into would hide exactly the mismatches worth catching — a renamed
/// property or a changed discriminator would rename itself on both sides and the test would still
/// pass.
/// </remarks>
internal static class WorkflowRequests
{
    /// <summary>A minimal valid LLM step, named <paramref name="name"/>.</summary>
    internal static object LlmStep(string name) => new
    {
        name,
        type = "LlmCall",
        configuration = new { type = "llm_call", systemPrompt = "do the thing", modelDeploymentKey = "gpt-4o" }
    };

    /// <summary>A sub-plan step invoking <paramref name="childWorkflowId"/>.</summary>
    internal static object SubPlanStep(string name, Guid childWorkflowId) => new
    {
        name,
        type = "SubPlanInvocation",
        configuration = new { type = "sub_plan", childWorkflowId }
    };

    /// <summary>A workflow definition wrapping <paramref name="steps"/> and optional <paramref name="edges"/>.</summary>
    internal static object Definition(object[] steps, object[]? edges = null, string name = "test-workflow") => new
    {
        name,
        steps,
        edges = edges ?? []
    };
}
