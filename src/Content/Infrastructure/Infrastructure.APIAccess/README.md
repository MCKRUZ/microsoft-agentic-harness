# Infrastructure.APIAccess

Every outgoing HTTP call and incoming API request in the harness flows through infrastructure defined here. This project provides the HTTP client pipeline (resilience, correlation, compression, logging), the permission-based authorization system, and the service discovery layer that resolves API endpoints at runtime.

---

## The HTTP Client Pipeline

Outgoing HTTP requests pass through a chain of delegating handlers, each adding a cross-cutting concern:

**CorrelationIdDelegatingHandler** propagates correlation IDs to outgoing requests via headers. When the harness calls Azure OpenAI, the correlation ID from the original user request follows — enabling end-to-end tracing across service boundaries.

**UserAgentDelegatingHandler** sets a consistent `User-Agent` header derived from assembly metadata (app name, version, OS). External APIs can identify the harness in their logs.

**LoggingDelegatingHandler** logs every outgoing request at Debug level — method, URI, and response status. Useful for debugging third-party API failures without reaching for Fiddler.

**DefaultHttpClientHandler** enables Brotli/Deflate/GZip decompression and optionally bypasses certificate validation in development environments.

## Resilience Policies

`IServiceCollectionExtensions.AddResiliencePipelines()` configures Polly-based resilience for outgoing HTTP calls:

- **Retry** — Exponential backoff with jitter for transient failures (`SocketException`, `HttpRequestException`, `TimeoutRejectedException`, `BrokenCircuitException`)
- **Timeout** — Per-request timeout, configurable via `AppConfig.Http.Policies.HttpTimeout`
- **Circuit Breaker** — Trips open after configurable failure ratio, prevents cascade overload

These are registered as named HTTP client policies — any `IHttpClientFactory`-created client can opt into them. Configuration lives in `AppConfig.Http.Policies`:

```json
{
  "Http": {
    "Policies": {
      "HttpTimeout": { "Timeout": "00:01:00" },
      "HttpRetry": { "Count": 3, "Delay": "00:00:01" },
      "HttpCircuitBreaker": { "FailureRatio": 0.1, "DurationOfBreak": "00:00:30" }
    }
  }
}
```

## Rate Limiting

`AddRateLimitingPolicies()` configures fixed-window rate limiting for two endpoint categories: AI endpoints (higher throughput) and MCP endpoints (standard throughput). Applied at the ASP.NET Core middleware level.

## Permission-Based Authorization

The authorization system maps domain permissions to ASP.NET Core policies without requiring every combination to be pre-registered:

**PermissionAuthorizeAttribute** marks endpoints with required permissions from the `AuthPermissions` enum. It encodes permissions into policy names like `"Permission0-2"`.

```csharp
[PermissionAuthorize(AuthPermissions.Admin)]
public class AdminEndpoints { }

[PermissionAuthorize(AuthPermissions.TermsAgreement, AuthPermissions.Access)]
public class UserEndpoints { }
```

**PermissionPolicyProvider** dynamically parses these encoded policy names at runtime and constructs policies with `PermissionRequirement` instances. No static registration needed.

**PermissionAuthHandler** evaluates requirements against user claims: `Access` is always granted, `TermsAgreement` checks for a terms-agreed claim, `Admin` checks for an admin role claim.

## Service Discovery

`ApiEndpointResolverService` resolves typed HTTP client configurations from `HttpConfig`. When a client is created, the resolver checks the primary endpoint's health. If it's down, it falls back to configured alternative endpoints. Resolved endpoints are cached per the client config's duration setting.

## Adding New HTTP Clients

1. Add a configuration class extending `HttpClientConfig` in `Domain.Common.Config.Http`
2. Add a constant in `ApiAccessConstants.cs` for the configuration section name
3. Update `GetClientConfig` in `ApiEndpointResolverService` to resolve the new config
4. Create the client interface in `Application.Common/Interfaces/`
5. Implement the client and register it using `AddHttpClient<T,TImpl,TOpts>`

---

## Project Structure

```
Infrastructure.APIAccess/
├── Auth/
│   ├── Attributes/
│   │   └── PermissionAuthorizeAttribute.cs  Declarative permission marking
│   ├── Handlers/
│   │   └── PermissionAuthHandler.cs         Claim-based permission evaluation
│   ├── Providers/
│   │   └── PermissionPolicyProvider.cs      Dynamic policy construction
│   └── Requirements/
│       └── PermissionRequirement.cs         AuthPermissions value container
├── Common/
│   ├── ApiAccessConstants.cs                Config section names
│   ├── Extensions/
│   │   ├── IEndpointConventionBuilderExtensions.cs  Filter composition helper
│   │   └── IServiceCollectionExtensions.cs          Kestrel, versioning, Swagger, resilience, rate limiting
│   └── Helpers/
│       └── EndpointFilterHelper.cs          Standard filter pipeline factory
├── Handlers/
│   ├── CorrelationIdDelegatingHandler.cs    Request correlation propagation
│   ├── DefaultHttpClientHandler.cs          Compression + dev cert bypass
│   ├── LoggingDelegatingHandler.cs          Debug-level request logging
│   └── UserAgentDelegatingHandler.cs        Assembly-derived User-Agent
├── Services/
│   └── ApiEndpointResolverService.cs        Health-check-based endpoint discovery
└── DependencyInjection.cs
```

## Dependencies

- **Application.Common** — Exception types, config interfaces
- **Infrastructure.Common** — Endpoint filters, claim extensions
- **Domain.Common** — `AppConfig.Http` configuration, `AuthPermissions` enum
- **Polly** — Resilience policies (retry, timeout, circuit breaker)
- **CorrelationId** — Request correlation middleware
- **Swashbuckle** — OpenAPI/Swagger generation
- **Asp.Versioning** — API versioning (header-based via `X-Api-Version`)
