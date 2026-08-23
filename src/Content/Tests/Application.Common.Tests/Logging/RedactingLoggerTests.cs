using Application.Common.Logging;
using Domain.Common.Logging;
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
        public object? LastLogState { get; private set; }

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
            LastLogState = state;
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
    /// CI's correctness-review finding: <c>Log</c> used to re-invoke the inner logger with the rendered
    /// message collapsed to a plain string, discarding the original <c>state</c> object. Anything that
    /// reads <c>state</c> directly rather than calling the formatter — the OTel logging bridge extracts
    /// <c>LogRecord.Attributes</c>/<c>{OriginalFormat}</c> exactly this way — silently lost every
    /// structured field. This proves the inner logger now receives the ORIGINAL state object, unchanged,
    /// while still rendering the redacted text through whatever formatter is supplied.
    /// </summary>
    [Fact]
    public void Log_PreservesOriginalStateObject_ForStructuredAttributeExtraction()
    {
        var inner = new CapturingLogger();
        var redactor = new FixedRedactor(enabled: true, redact: t => t.Replace("secret-value", "[REDACTED]"));
        var logger = new RedactingLogger(inner, redactor);

        logger.LogInformation("token is {Token}", "secret-value");

        inner.LastLogState.Should().NotBeOfType<string>(
            "state must not be collapsed to a string — a structured-attribute reader needs the original shape");
        inner.LastMessage.Should().Be("token is [REDACTED]",
            "the rendered text must still be redacted, regardless of what shape state itself keeps");
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

    /// <summary>
    /// CI's correctness-review finding on the fix above: the prior version's fallback branch collapsed
    /// ANY unrecognized scope object to a redacted string via <see cref="object.ToString"/> — including
    /// this codebase's own <c>Domain.Common.Logging.ExecutionScope</c> (executor/correlation ids, a step
    /// number: structural identifiers, never free text). That silently swapped the pushed scope object's
    /// runtime TYPE, breaking <c>ExecutionScopeProvider.GetCurrentScope</c>'s <c>scope is ExecutionScope</c>
    /// check for every request, for every local sink, whenever redaction is enabled (the default) — not
    /// a fidelity loss but a correctness regression in an already-wired feature. This proves a scope
    /// object that is neither a string nor a structured key/value collection passes through as the exact
    /// same instance.
    /// </summary>
    [Fact]
    public void BeginScope_UnrecognizedObjectScope_PassesThroughAsTheSameInstance()
    {
        var inner = new CapturingLogger();
        var redactor = new FixedRedactor(enabled: true, redact: _ => "SHOULD NOT BE CALLED");
        var logger = new RedactingLogger(inner, redactor);
        var scope = new DomainStyleScope("exec-1", "corr-1");

        logger.BeginScope(scope);

        inner.LastScopeState.Should().BeSameAs(scope,
            "an unrecognized scope type must pass through untouched, not be collapsed to a string");
    }

    /// <summary>Stands in for a real domain scope type (e.g. ExecutionScope) — not a string, not a KVP collection.</summary>
    private sealed record DomainStyleScope(string ExecutorId, string CorrelationId);

    /// <summary>
    /// The exact real-world scenario CI's correctness-review flagged, against the actual production
    /// type: <see cref="ExecutionScopeProvider.GetCurrentScope"/> requires the pushed scope object to
    /// still <em>be</em> an <see cref="ExecutionScope"/> instance (a type check, not a shape check), so
    /// it must reach the scope stack unchanged.
    /// </summary>
    [Fact]
    public void BeginScope_ExecutionScope_PassesThroughSoDownstreamTypeCheckStillMatches()
    {
        // Wired directly to the scope provider, not via LoggerFactory: LoggerFactory owns and injects
        // its own IExternalScopeProvider into any ISupportExternalScope provider, which would silently
        // replace this one — the point of this test is what the scope stack itself sees.
        var scopeProvider = new ExecutionScopeProvider();
        var sinkLogger = new SinkLogger(scopeProvider);
        var redactor = new FixedRedactor(enabled: true, redact: _ => "SHOULD NOT BE CALLED");
        var logger = new RedactingLogger(sinkLogger, redactor);

        using (logger.BeginScope(new ExecutionScope(ExecutorId: "exec-1", CorrelationId: "corr-1")))
        {
            var current = ExecutionScopeProvider.GetCurrentScope(scopeProvider);
            current.Should().NotBeNull("the scope-provider's own type check must still recognize the pushed object");
            current!.ExecutorId.Should().Be("exec-1");
            current.CorrelationId.Should().Be("corr-1");
        }
    }

    /// <summary>Minimal logger that delegates scope pushes directly to the given <see cref="IExternalScopeProvider"/>.</summary>
    private sealed class SinkLogger(IExternalScopeProvider scopeProvider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopeProvider.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f) { }
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
