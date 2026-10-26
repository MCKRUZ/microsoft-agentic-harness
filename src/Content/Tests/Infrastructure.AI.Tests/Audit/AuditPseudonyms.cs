using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Audit;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// The real <see cref="HmacAuditPseudonymizer"/> under a fixed test key, for tests of the audit stores that record
/// pseudonyms (#903). A fixed key lets a test compute the reference it expects instead of only checking that the raw
/// value is absent.
/// </summary>
internal static class AuditPseudonyms
{
    /// <summary>The bytes 0x01..0x20: a test key, never a real one.</summary>
    internal static readonly string TestKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    /// <summary>A pseudonymizer under <see cref="TestKey"/>.</summary>
    internal static HmacAuditPseudonymizer Create(string? key = null) => new(
        Options.Create(new AppConfig { AI = new AIConfig { Audit = new AuditConfig { PseudonymKey = key ?? TestKey } } }),
        new OwnerOnlyDirectoryCreator(),
        NullLogger<HmacAuditPseudonymizer>.Instance);
}
