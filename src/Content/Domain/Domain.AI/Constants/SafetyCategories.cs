namespace Domain.AI.Constants;

/// <summary>
/// Well-known content safety violation categories used by safety middleware.
/// </summary>
public static class SafetyCategories
{
    public const string Hate = "hate";
    public const string Violence = "violence";
    public const string SelfHarm = "self-harm";
    public const string Sexual = "sexual";
    public const string Pii = "pii";
    public const string Jailbreak = "jailbreak";
}
