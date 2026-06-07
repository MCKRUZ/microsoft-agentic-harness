using Domain.Common.Config;
using Domain.Common.Config.AI;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tests.Egress.Support;

/// <summary>
/// Builds <see cref="IOptionsMonitor{AppConfig}"/> pointed at a temp directory
/// for egress audit, mirroring <c>Changes/Support/TestConfig.cs</c>.
/// </summary>
internal static class TestConfig
{
    public static (IOptionsMonitor<AppConfig> Monitor, string TempDir) NewMonitor(
        params EgressAllowlistConfigEntry[] defaultAllowlist)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "agentic-harness-egress-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var cfg = new AppConfig();
        cfg.AI.Egress.AuditStoragePath = Path.Combine(tempDir, "audit");
        cfg.AI.Egress.Enabled = true;
        cfg.AI.Egress.DefaultAllowlist = defaultAllowlist.ToList();
        return (new StaticOptionsMonitor<AppConfig>(cfg), tempDir);
    }

    public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
