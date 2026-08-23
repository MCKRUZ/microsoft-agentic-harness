using Application.Common.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Application.Common.Tests.Logging;

/// <summary>
/// Tests for <see cref="RedactingLogger"/> (#457) — the local-sink counterpart to the OTel logging
/// bridge's own redaction, proven against a captured inner <see cref="ILogger"/> the way the real
/// console/file/JSONL sinks would receive the call.
/// </summary>
public sealed class RedactingLoggerTests
{
    /// <summary>Captures exactly what a real local sink would have seen.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public string? LastMessage { get; private set; }
        public Exception? LastException { get; private set; }
        public object? LastScopeState { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            LastScopeState = state;
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LastMessage = formatter(state, exception);
            LastException = exception;
        }
    }

    private sealed class FixedRedactor(bool enabled, Func<string, string> redact) : ILocalLogRedactor
    {
        public bool Enabled => enabled;
        public string Redact(string text) => redact(text);
    }

    [Fact]
    public void Log_RedactorEnabled_RedactsFormattedMessage()
    {
        var inner = new CapturingLogger();
        var redactor = new FixedRedactor(enabled: true, redact: t => t.Replace("secret-value", "[REDACTED]"));
        var logger = new RedactingLogger(inner, redactor);

        logger.LogInformation("token is {Token}", "secret-value");

        inner.LastMessage.Should().Be("token is [REDACTED]");
    }

    [Fact]
    public void Log_ExceptionCarriesMatch_ReplacesExceptionWithRedactedText()
    {
        var inner = new CapturingLogger();
        var ex = new InvalidOperationException("connection string: secret-value");
        var redactor = new FixedRedactor(enabled: true, redact: t => t.Replace("secret-value", "[REDACTED]"));
        var logger = new RedactingLogger(inner, redactor);

        logger.LogWarning(ex, "operation failed");

        inner.LastException.Should().NotBeNull();
        inner.LastException.Should().NotBeSameAs(ex, "the original exception must not reach the inner sink unredacted");
        inner.LastException!.ToString().Should().Contain("[REDACTED]");
        inner.LastException.ToString().Should().NotContain("secret-value");
    }

    [Fact]
    public void Log_ExceptionMessageClean_PassesTheOriginalExceptionThrough()
    {
        var inner = new CapturingLogger();
        var ex = new InvalidOperationException("nothing sensitive here");
        var redactor = new FixedRedactor(enabled: true, redact: t => t); // no-op
        var logger = new RedactingLogger(inner, redactor);

        logger.LogWarning(ex, "operation failed");

        // No match, no replacement — the original exception object is preserved rather than always
        // wrapped, so a caller catching further up the chain still sees the real exception type.
        inner.LastException.Should().BeSameAs(ex);
    }

    [Fact]
    public void Log_RedactorDisabled_PassesThroughUnchanged()
    {
        var inner = new CapturingLogger();
        var redactor = new FixedRedactor(enabled: false, redact: _ => "SHOULD NOT BE CALLED");
        var logger = new RedactingLogger(inner, redactor);

        logger.LogInformation("token is {Token}", "secret-value");

        inner.LastMessage.Should().Be("token is secret-value");
    }

    /// <summary>
    /// #457's motivating scenario: an inner exception's message survives even when the outer
    /// exception's own message is clean — <c>Exception.ToString()</c> recursively includes the
    /// " ---&gt; " inner-exception chain, so redacting only <c>.Message</c> would miss this.
    /// </summary>
    [Fact]
    public void Log_SecretInInnerException_StillRedacted()
    {
        var inner = new CapturingLogger();
        var innerEx = new InvalidOperationException("connection string: secret-value");
        var outerEx = new InvalidOperationException("dispatch failed", innerEx);
        var redactor = new FixedRedactor(enabled: true, redact: t => t.Replace("secret-value", "[REDACTED]"));
        var logger = new RedactingLogger(inner, redactor);

        logger.LogError(outerEx, "delegation threw");

        inner.LastException!.ToString().Should().Contain("[REDACTED]");
        inner.LastException.ToString().Should().NotContain("secret-value");
    }

    /// <summary>
    /// Independent security review finding: <c>BeginScope</c> forwarded scope state unchanged, so a
    /// secret or PII value placed in a structured scope (a connection string, an email) reached every
    /// local sink in cleartext even with redaction enabled — a formatter that renders scope contents
    /// (e.g. a console formatter with <c>IncludeScopes = true</c>) bypassed #457's guarantee entirely.
    /// This proves a string-valued entry in a structured (key/value) scope is redacted.
    /// </summary>
    [Fact]
    public void BeginScope_StructuredScopeWithSecretValue_RedactsTheStringEntries()
    {
        var inner = new CapturingLogger();
        var redactor = new FixedRedactor(enabled: true, redact: t => t.Replace("secret-value", "[REDACTED]"));
        var logger = new RedactingLogger(inner, redactor);

        var scope = new Dictionary<string, object?> { ["ConnectionString"] = "secret-value", ["Count"] = 3 };
        logger.BeginScope(scope);

        var captured = inner.LastScopeState.Should().BeAssignableTo<IEnumerable<KeyValuePair<string, object?>>>().Subject;
        var pairs = captured.ToDictionary(kv => kv.Key, kv => kv.Value);
        pairs["ConnectionString"].Should().Be("[REDACTED]");
        pairs["Count"].Should().Be(3, "non-string values pass through unchanged — nothing to redact");
    }

    /// <summary>A plain (non-structured) scope value is redacted via its rendered text.</summary>
    [Fact]
    public void BeginScope_PlainScopeValue_IsRedactedViaItsRenderedText()
    {
        var inner = new CapturingLogger();
        var redactor = new FixedRedactor(enabled: true, redact: t => t.Replace("secret-value", "[REDACTED]"));
        var logger = new RedactingLogger(inner, redactor);

        logger.BeginScope("token is secret-value");

        inner.LastScopeState.Should().Be("token is [REDACTED]");
    }

    [Fact]
    public void BeginScope_RedactorDisabled_PassesScopeThroughUnchanged()
    {
        var inner = new CapturingLogger();
        var redactor = new FixedRedactor(enabled: false, redact: _ => "SHOULD NOT BE CALLED");
        var logger = new RedactingLogger(inner, redactor);
        var scope = new Dictionary<string, object?> { ["ConnectionString"] = "secret-value" };

        logger.BeginScope(scope);

        inner.LastScopeState.Should().BeSameAs(scope);
    }
}
