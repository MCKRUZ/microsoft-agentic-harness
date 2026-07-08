# Infrastructure.APIAccess

The HTTP pipeline and authorization layer for the Agentic Harness. Every outgoing HTTP call flows through this project's delegating handler chain (correlation, compression, logging, user-agent), and every API endpoint optionally uses its permission-based authorization system. It also provides production-ready server configuration: Kestrel limits, API versioning, Swagger/OpenAPI generation, rate limiting, CORS policies, and Polly resilience pipelines.

This project sits in the Infrastructure layer. It references `Application.Common` for configuration DTOs and `Infrastructure.Common` for endpoint filters. It is consumed exclusively by `Presentation.Common`, which wraps its extension methods into a single facade call.

## Architecture Context

```
Presentation.AgentHub (Program.cs)
        |
        v
Presentation.Common.DependencyInjection
  calls AddPresentationCommonDependencies(httpConfig)
        |
        v
Infrastructure.APIAccess.Common.Extensions.IServiceCollectionExtensions
  - AddCustomKestrelServerOptions()
  - AddCustomApiVersioning()
  - AddCustomSwaggerGen(httpConfig)
  - AddCustomRateLimiter()
  - AddCustomCorsPolicy(httpConfig)
        |
Infrastructure.APIAccess.DependencyInjection
  - AddDefaultHttpClient()  [handler pipeline + resilience]
  - PermissionPolicyProvider + PermissionAuthHandler
        |
        v
Domain.Common.Config.Http  (configuration POCOs)
Application.Common         (exception types, IIdentityService)
Infrastructure.Common      (HttpAuthEndpointFilter, ClaimExtensions)
```

## Key Concepts

### Permission-Based Authorization

The harness avoids pre-registering one authorization policy per permission combination. Instead, it uses a dynamic system with three collaborating types:

**1. PermissionAuthorizeAttribute** encodes enum values into a policy name string:

```csharp
// Endpoint requires both Access and Admin permissions
[PermissionAuthorize(AuthPermissions.Access, AuthPermissions.Admin)]
public async Task<IResult> AdminEndpoint() { ... }
// Generates policy name: "Permission0-2"
```

**2. PermissionPolicyProvider** intercepts unknown policy names at runtime, parses the encoded integers back into `AuthPermissions` values, and builds a policy dynamically:

```csharp
// "Permission0-2" → requirements for AuthPermissions.Access + AuthPermissions.Admin
var builder = new AuthorizationPolicyBuilder();
builder.AddRequirements(new PermissionRequirement(AuthPermissions.Access));
builder.AddRequirements(new PermissionRequirement(AuthPermissions.Admin));
builder.RequireAuthenticatedUser();
```

**3. PermissionAuthHandler** evaluates each requirement against the user's claims:
- `Access` -- always granted for authenticated users
- `TermsAgreement` -- checks `ClaimConstants.AgreedToTerms` claim
- `Admin` -- checks `ClaimConstants.IsAdmin` claim

### HTTP Client Handler Pipeline

Every `HttpClient` created via `IHttpClientFactory` inherits four delegating handlers in this order:

1. **CorrelationIdDelegatingHandler** -- reads the current request's correlation ID and attaches it to the outgoing request header, enabling distributed tracing across Azure OpenAI, MCP servers, and Content Safety calls.
2. **LoggingDelegatingHandler** -- logs method + URI at Debug level for troubleshooting without Fiddler.
3. **UserAgentDelegatingHandler** -- stamps outgoing requests with `{ProductName}/{Version} ({OS})` derived from assembly metadata.
4. **DefaultHttpClientHandler** -- configures Brotli/Deflate/GZip decompression. In Development, bypasses certificate validation for self-signed localhost certs.

### Polly Resilience Pipeline

`AddCustomResilienceHandler()` attaches five strategies to every client created via
`IHttpClientFactory` (wired once in `AddDefaultHttpClient()` through
`ConfigureHttpClientDefaults`, so each request actually executes through the pipeline — do
not attach it a second time per client, or retries compound multiplicatively):

| Strategy | Behavior | Config Source |
|----------|----------|---------------|
| Total Timeout | Bounds the whole operation across all attempts + backoff; `HttpClient.Timeout` is set to infinite so it cannot race the pipeline and truncate the retry budget | `HttpTimeout.TotalTimeout` (computed from the retry budget when unset) |
| Retry | Exponential backoff + jitter for `SocketException`, `HttpRequestException`, `TimeoutRejectedException` — **idempotent methods only (GET/HEAD/OPTIONS)**. POST/PATCH/PUT/DELETE are never retried: a transient failure can surface after the server already processed the request, so a retry would duplicate side effects. Circuit-breaker and rate-limiter rejections are not retried — they come from strategies inside this same pipeline | `HttpRetry.Count`, `HttpRetry.Delay` |
| Per-Attempt Timeout | Cancels a single slow attempt so the retry strategy can start the next one | `HttpTimeout.Timeout` |
| Circuit Breaker | Opens after failure ratio exceeds threshold; 30s sampling window | `HttpCircuitBreaker.FailureRatio`, `.DurationOfBreak` |
| Rate Limiter | Sliding window: 100 requests/min, 4 segments | Hardcoded |

Note: the `Microsoft.Extensions.Http.Resilience` handler snapshots requests, which makes them
**re-sendable** — it does not make resending **safe**. Safety comes from the idempotent-method
restriction above.

### API Endpoint Resolution

`ApiEndpointResolverService` provides health-check-based service discovery. When `EnableServiceDiscovery` is true in a client config, it tests all endpoints (primary + alternatives) with a `HEAD` request and picks the fastest healthy one. Results are cached per `CacheDuration`.

### Server Configuration Extensions

| Method | What It Configures |
|--------|-------------------|
| `AddCustomKestrelServerOptions()` | 100 max connections, 10 MB body limit, 1 min header timeout |
| `AddCustomApiVersioning()` | Header-based `X-Api-Version`, defaults to 1.0 |
| `AddCustomSwaggerGen(httpConfig)` | OpenAPI doc with security scheme (when enabled) |
| `AddCustomRateLimiter()` | Fixed-window: 100 req/min for AI and MCP policies |
| `AddCustomCorsPolicy(httpConfig)` | 4 named policies: default, config, AI copilot, MCP server |

## Project Structure

```
Infrastructure.APIAccess/
├── Auth/
│   ├── Attributes/
│   │   └── PermissionAuthorizeAttribute.cs    Encodes permissions → policy name
│   ├── Handlers/
│   │   └── PermissionAuthHandler.cs           Evaluates claims against requirements
│   ├── Providers/
│   │   └── PermissionPolicyProvider.cs        Parses policy names → builds policies
│   └── Requirements/
│       └── PermissionRequirement.cs           Single-permission IAuthorizationRequirement
├── Common/
│   ├── ApiAccessConstants.cs                  Typed client config section names
│   ├── Extensions/
│   │   ├── IEndpointConventionBuilderExtensions.cs  .AddFilters() for minimal APIs
│   │   └── IServiceCollectionExtensions.cs    Kestrel, versioning, Swagger, CORS, resilience
│   └── Helpers/
│       └── EndpointFilterHelper.cs            Factory: error + auth filter arrays
├── Handlers/
│   ├── CorrelationIdDelegatingHandler.cs      Propagates X-Correlation-Id
│   ├── DefaultHttpClientHandler.cs            Decompression + dev cert bypass
│   ├── LoggingDelegatingHandler.cs            Debug-level request logging
│   └── UserAgentDelegatingHandler.cs          Assembly-derived User-Agent
├── Services/
│   └── ApiEndpointResolverService.cs          Health-check endpoint discovery + caching
└── DependencyInjection.cs                     AddInfrastructureApiAccessDependencies()
```

## Key Types Reference

| Type | Purpose | Consumed By |
|------|---------|-------------|
| `PermissionAuthorizeAttribute` | Declarative permission enforcement | Any API controller/endpoint |
| `PermissionPolicyProvider` | Runtime policy creation | ASP.NET Core auth middleware |
| `PermissionAuthHandler` | Claims-based permission evaluation | ASP.NET Core auth middleware |
| `ApiEndpointResolverService` | Health-check endpoint resolution | Typed HTTP client registration |
| `IServiceCollectionExtensions` | Server config (Kestrel, CORS, Swagger, etc.) | `Presentation.Common.DependencyInjection` |
| `EndpointFilterHelper` | Pre-built auth + error filter arrays | Minimal API endpoint registration |
| `DefaultHttpClientHandler` | Primary message handler for all clients | `IHttpClientFactory` pipeline |

## Configuration

All configuration lives under `AppConfig:Http` in appsettings.json:

```json
{
  "AppConfig": {
    "Http": {
      "CorsAllowedOrigins": "http://localhost:5173;http://localhost:5174",
      "Authorization": {
        "Enabled": true,
        "HttpHeaderName": "X-API-Key",
        "AccessKey1": "-- from user-secrets --",
        "AccessKey2": "-- for zero-downtime rotation --"
      },
      "Policies": {
        "HttpRetry": { "Count": 3, "Delay": "00:00:02" },
        "HttpTimeout": { "Timeout": "00:00:30" },
        "HttpCircuitBreaker": { "FailureRatio": 0.5, "DurationOfBreak": "00:00:30" }
      },
      "HttpSwagger": {
        "OpenApiEnabled": true,
        "ServiceAuthorizationEnabled": false,
        "OpenApiSpec": {
          "SpecName": "v1",
          "HttpOpenApiInfo": {
            "Title": "Agentic Harness API",
            "Version": "1.0",
            "Description": "AI Agent orchestration API"
          }
        }
      }
    }
  }
}
```

## How to Run

This project is a class library -- it doesn't run independently. It's consumed by Presentation hosts:

```bash
# Build to verify compilation
dotnet build src/Content/Infrastructure/Infrastructure.APIAccess

# Run via the AgentHub host
dotnet run --project src/Content/Presentation/Presentation.AgentHub
```

## Common Tasks

### Adding a New Typed HTTP Client

1. Define a config class in `Domain.Common.Config.Http` extending `HttpClientConfig`
2. Add a section name constant in `ApiAccessConstants.cs`
3. Map it in `ApiEndpointResolverService.GetClientConfig<T>()`
4. Register in the consuming project:

```csharp
services.AddHttpClient<IMyService, MyService, MyServiceConfig>(
    ApiAccessConstants.MyServiceSection);
```

### Adding a New CORS Policy

Add a new `options.AddPolicy(...)` block in `AddCustomCorsPolicy()` and reference the policy name from `PolicyNameConstants` in Domain.Common.

### Adding a New Permission Level

1. Add a value to `AuthPermissions` enum in Domain.Common
2. Add a case in `PermissionAuthHandler.PermissionRequirementsMet()`
3. Use it: `[PermissionAuthorize(AuthPermissions.NewLevel)]`

## Dependencies

**Project References:**
- `Application.Common` -- `HttpConfig`, `HttpClientConfig`, `AuthPermissions` enum, exception types
- `Infrastructure.Common` -- `HttpAuthEndpointFilter`, `HttpErrorEndpointFilter`, `ClaimExtensions`

**NuGet Packages:**
- `Asp.Versioning.Http` / `Asp.Versioning.Mvc.ApiExplorer` -- header-based API versioning
- `CorrelationId` -- correlation ID middleware and forwarding
- `Microsoft.Extensions.Http.Resilience` -- Polly integration for `IHttpClientFactory`
- `Swashbuckle.AspNetCore.SwaggerGen` -- OpenAPI document generation

## Testing

**Test project:** `Tests/Infrastructure.APIAccess.Tests`

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Infrastructure.APIAccess"
```

**Coverage areas:**
- Permission policy encoding/decoding round-trip
- Handler pipeline (correlation propagation, user-agent header format)
- Endpoint resolver caching and health-check fallback
- Rate limiter policy activation
- CORS origin matching
