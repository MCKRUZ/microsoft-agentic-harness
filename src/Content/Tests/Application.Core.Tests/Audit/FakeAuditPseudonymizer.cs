using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.Audit;

namespace Application.Core.Tests.Audit;

/// <summary>
/// A deterministic stand-in for the audit pseudonymizer, for handler tests in this project, which cannot reference the
/// real one in Infrastructure. Shaped like the real output so a test can assert the reference was recorded in place of
/// the raw value.
/// </summary>
internal sealed class FakeAuditPseudonymizer : IAuditPseudonymizer
{
    /// <inheritdoc />
    public string KeyId => "testkey0";

    /// <summary>The reference this fake makes for <paramref name="identifier"/>.</summary>
    public static string PersonOf(string identifier) => $"ref:testkey0:{Hex(identifier.ToUpperInvariant())}";

    /// <inheritdoc />
    public string Person(string identifier) => PersonOf(identifier);

    /// <inheritdoc />
    public string Fingerprint(string purpose, string value) => $"fp:testkey0:{Hex(purpose + "|" + value)}";

    private static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
