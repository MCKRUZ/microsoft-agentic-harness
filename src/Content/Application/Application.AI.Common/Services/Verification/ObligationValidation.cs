namespace Application.AI.Common.Services.Verification;

/// <summary>The result of <see cref="ObligationValidator.Validate"/>.</summary>
public sealed record ObligationValidation(bool IsValid, ObligationRejectionReason? RejectionReason = null)
{
    /// <summary>The obligation is well-formed and may be dispatched to a verifier.</summary>
    public static ObligationValidation Valid() => new(true);

    /// <summary>The obligation is malformed and must not be dispatched.</summary>
    public static ObligationValidation Rejected(ObligationRejectionReason reason) => new(false, reason);
}
