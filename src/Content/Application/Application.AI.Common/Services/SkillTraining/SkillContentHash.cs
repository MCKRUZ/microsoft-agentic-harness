using System.Security.Cryptography;
using System.Text;

namespace Application.AI.Common.Services.SkillTraining;

/// <summary>
/// The one definition of a skill document's hash: SHA-256 of the UTF-8 text, lower-case hex with no
/// separators. Checkpoints, run fingerprints and experience entries must all agree on it.
/// </summary>
public static class SkillContentHash
{
    /// <summary>Hashes <paramref name="content"/>.</summary>
    /// <param name="content">The skill document.</param>
    public static string Of(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
