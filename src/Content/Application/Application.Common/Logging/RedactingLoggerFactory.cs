using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// Wraps an <see cref="ILoggerFactory"/> so every <see cref="ILogger"/> it hands out is a
/// <see cref="RedactingLogger"/> — the one front door every local sink (current or future) is reached
/// through, closing #457 in a single place rather than patching each <see cref="ILoggerProvider"/>
/// individually.
/// </summary>
/// <remarks>
/// Wrapping the factory rather than each provider is what makes this cover providers this decorator
/// was never told about: <see cref="AddProvider"/> forwards to the inner factory unchanged, so a
/// provider added after this wrapper is constructed — by a consumer's own host code, not just this
/// harness's built-in ones — is still reached through <see cref="CreateLogger"/>, and therefore still
/// redacted.
/// </remarks>
public sealed class RedactingLoggerFactory : ILoggerFactory
{
    private readonly ILoggerFactory _inner;
    private readonly ILocalLogRedactor _redactor;

    public RedactingLoggerFactory(ILoggerFactory inner, ILocalLogRedactor redactor)
    {
        _inner = inner;
        _redactor = redactor;
    }

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        new RedactingLogger(_inner.CreateLogger(categoryName), _redactor);

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}
