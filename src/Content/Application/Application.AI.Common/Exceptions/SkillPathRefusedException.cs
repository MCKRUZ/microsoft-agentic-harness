using Application.Common.Exceptions;

namespace Application.AI.Common.Exceptions;

/// <summary>
/// Thrown when a path is refused by the skill sandbox because it lies outside the configured skill
/// content roots.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="UnauthorizedAccessException"/> on purpose. Skill discovery is
/// deliberately tolerant — a malformed manifest is skipped so its siblings still load — but that
/// tolerance must not extend to a sandbox refusal, because an empty result there is
/// indistinguishable from a directory that genuinely holds no skills, and a misconfigured root would
/// boot an agent silently missing its own skills.
/// </para>
/// <para>
/// Filtering on <see cref="UnauthorizedAccessException"/> instead would conflate the two cases the
/// distinction exists to separate: the operating system raises that same type for an ordinary
/// file-permission denial on a directory that is perfectly inside the sandbox. Treating that as a
/// refusal would turn a routine ACL problem into a startup abort. Catching this type means "the
/// sandbox said no", and nothing else.
/// </para>
/// <para>
/// Declares its own HTTP status rather than inheriting one. Deriving from
/// <see cref="UnauthorizedAccessException"/> would otherwise surface it as 401, which tells a caller
/// to authenticate — but the caller already is authenticated, and no amount of re-authenticating
/// widens a sandbox. 403 says the right thing: the request was understood and refused.
/// </para>
/// </remarks>
public sealed class SkillPathRefusedException : UnauthorizedAccessException, IHttpStatusException
{
    /// <summary>403 Forbidden. See the type remarks for why this is not 401.</summary>
    public int StatusCode => 403;

    /// <summary>
    /// What a production caller is told. Deliberately says nothing about which path was refused or
    /// where the sandbox boundary lies — that is the detail the refusal exists to withhold.
    /// </summary>
    public string SafeMessage => "Forbidden.";

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillPathRefusedException"/> class.
    /// </summary>
    /// <param name="message">A description of why the path was refused.</param>
    public SkillPathRefusedException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillPathRefusedException"/> class.
    /// </summary>
    /// <param name="message">A description of why the path was refused.</param>
    /// <param name="innerException">The refusal raised by the underlying sandbox.</param>
    public SkillPathRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
