namespace Domain.AI.Telemetry.Conventions;

/// <summary>Content safety telemetry attributes and metric names.</summary>
public static class SafetyConventions
{
    public const string Phase = "agent.safety.phase";
    public const string Filter = "agent.safety.filter";
    public const string Outcome = "agent.safety.outcome";
    public const string Category = "agent.safety.category";
    public const string Severity = "agent.safety.severity";
    public const string Evaluations = "agent.safety.evaluations";
    public const string Blocks = "agent.safety.blocks";

    public static class PhaseValues
    {
        public const string Prompt = "prompt";
        public const string Response = "response";
    }

    public static class OutcomeValues
    {
        public const string Pass = "pass";
        public const string Block = "block";
        public const string Redact = "redact";
    }

    /// <summary>
    /// Well-known safety category identifiers used in <c>ContentSafetyException</c>
    /// and content safety telemetry. Use these instead of raw strings.
    /// </summary>
    public static class CategoryValues
    {
        public const string Hate = "hate";
        public const string Violence = "violence";
        public const string SelfHarm = "self-harm";
        public const string Sexual = "sexual";
        public const string Pii = "pii";
        public const string Jailbreak = "jailbreak";
        public const string PromptInjection = "prompt-injection";
    }
}
