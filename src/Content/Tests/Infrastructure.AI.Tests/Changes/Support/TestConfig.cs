using Domain.Common.Config;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Tests.Changes.Support;

/// <summary>
/// Builds <see cref="IOptionsMonitor{AppConfig}"/> instances pointed at a
/// per-test temporary directory so the JSONL audit and the file-system
/// evidence store don't collide across parallel test runs.
/// </summary>
internal static class TestConfig
{
    public static (IOptionsMonitor<AppConfig> Monitor, string TempDir) NewMonitor()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentic-harness-changes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var cfg = new AppConfig();
        cfg.AI.Changes.AuditStoragePath = Path.Combine(tempDir, "audit");
        cfg.AI.Changes.EvidenceStoragePath = Path.Combine(tempDir, "evidence");
        cfg.AI.Changes.Enabled = true;
        cfg.AI.Changes.MaxConsecutiveDefers = 3;
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
