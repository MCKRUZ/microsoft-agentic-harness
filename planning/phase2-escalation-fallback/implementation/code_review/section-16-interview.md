# Section 16 Code Review Interview

## Auto-Fixes Applied
1. **HIGH-1 (Lazy caches faults)**: Switched to `LazyThreadSafetyMode.PublicationOnly` so transient factory errors don't permanently break the provider.
2. **HIGH-2 (Double-counted metrics)**: Made builder callbacks conditional — when `onCircuitStateChanged` is non-null, skip `RecordCircuitOpened`/`RecordCircuitClosed` (monitor handles metrics with richer tags). When null (standalone usage), keep builder metrics.
3. **MEDIUM-1 (Null logger cast)**: Replaced `_logger as ILogger<ResilientChatClient>` (always null) with `_loggerFactory.CreateLogger<ResilientChatClient>()`. Added `ILoggerFactory` constructor parameter.
4. **MEDIUM-4 (Unused ProviderCapabilityRegistry)**: Removed dead dependency. ResilientChatClient doesn't accept it in its constructor.

## Let Go
- **HIGH-3 (CancellationToken ignored)**: Accepted. The `Lazy<Task>` pattern makes propagation impractical. Composition runs once at startup. The interface defines `ct = default`.
- **MEDIUM-2 (Magic string `__resilientChatClient`)**: Consumer is wired in section-17 (governance-integration). Constant definition deferred to that section.
- **MEDIUM-3 (Concrete PollyProviderHealthMonitor)**: By design — documented in section plan. `ReportStateChange` is on the concrete type, not the interface. Future interface expansion deferred.
- **LOW-1 (No concurrent test)**: PublicationOnly mode fix makes this less critical. Integration test deferred to section-21.
- **LOW-2 (Null logger in tests)**: Minor style — not blocking.
- **LOW-3 (No factory wiring test)**: Integration test deferred to section-21.
