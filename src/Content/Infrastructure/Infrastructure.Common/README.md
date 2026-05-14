# Infrastructure.Common

The shared HTTP infrastructure that every web endpoint in the Agentic Harness depends on. This project provides the security baseline (headers, audit logging, CORS), structured exception handling, API key validation, and the identity service contract. It is small by design -- only truly cross-cutting HTTP concerns belong here. Domain-specific infrastructure (AI, MCP, connectors, observability) lives in its own dedicated project.

This project implements `IIdentityService` from `Application.Common` and provides middleware consumed by all Presentation hosts. `Infrastructure.APIAccess` references it for endpoint filter access, and `Presentation.Common` references it for middleware pipeline assembly.

## Architecture Context

```
Incoming HTTP Request
        |
        v
[SecurityAuditMiddleware]       ← Logs every request (method, path, status, IP, timing)
        |
        v
[SecurityHeadersMiddleware]     ← Adds X-Frame-Options, CSP, HSTS, etc.
        |
        v
[DynamicCorsMiddleware]         ← Evaluates Origin against runtime-configurable allowlist
        |
        v
[GlobalExceptionMiddleware]     ← Maps exceptions to structured HTTP responses
        |
        v
[Endpoint Filters]              ← HttpAuthEndpointFilter (API key) + HttpErrorEndpointFilter (413)
        |
        v
Application Logic (MediatR handlers)
```

**Dependency Direction:**
```
Presentation.Common → Infrastructure.Common → Application.Common → Domain.Common
                      Infrastructure.APIAccess ↗
```

## Key Concepts

### Security Headers Middleware

Applied to every response before the body is written. Prevents common web attacks without requiring per-endpoint configuration.

```csharp
// Registration in Presentation middleware pipeline:
app.UseMiddleware<SecurityHeadersMiddleware>();
```

**Headers applied:**

| Header | Value | Protects Against |
|--------|-------|-----------------|
| `X-Content-Type-Options` | `nosniff` | MIME-sniffing attacks |
| `X-Frame-Options` | `DENY` | Clickjacking via iframe |
| `X-XSS-Protection` | `1; mode=block` | Reflected XSS (legacy browsers) |
| `Referrer-Policy` | `no-referrer` | Referrer information leaks |
| `Content-Security-Policy` | `default-src 'self'...` | XSS, code injection |
| `Permissions-Policy` | `geolocation=(), microphone=(), camera=()` | Unauthorized API access |
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains; preload` | Protocol downgrade |

### Dynamic CORS Middleware

Unlike ASP.NET Core's built-in CORS (configured at startup), this middleware reads allowed origins from `IOptionsMonitor<AppConfig>` on **every request**. Configuration changes take effect immediately without restart.

```csharp
// appsettings.json -- semicolon-separated origins
"AppConfig": {
  "Http": {
    "CorsAllowedOrigins": "https://app.example.com;http://localhost:5173"
  }
}
```

Security constraints:
- No wildcard origins -- explicit allowlist only
- Requests from unlisted origins receive no CORS headers (browser blocks the response)
- Preflight `OPTIONS` requests short-circuit with 204 No Content
- `Vary: Origin` header prevents cache poisoning

### Global Exception Middleware

The outermost catch-all that maps domain exceptions to structured JSON responses. Exception type determines HTTP status code:

| Exception Type | Status Code | When It Fires |
|---------------|-------------|---------------|
| `NoContentException` | 204 | Query returned empty -- no error body |
| `BadRequestException` | 400 | Validation failures, malformed input |
| `UnauthorizedAccessException` | 401 | Missing/expired authentication |
| `ForbiddenAccessException` | 403 | Authenticated but insufficient permissions |
| `EntityNotFoundException` | 404 | Resource not found |
| `DatabaseInteractionException` | 422 | Data layer constraint violation |
| Any other exception (Dev) | 500 | Full stack trace in response |
| Any other exception (Prod) | 400 | Generic message -- no internal details leaked |

### API Key Endpoint Filter

`HttpAuthEndpointFilter` validates requests against dual API keys with **constant-time comparison** (prevents timing side-channel attacks). Dual keys enable zero-downtime rotation: deploy new key as `AccessKey2`, update clients, retire `AccessKey1`.

```csharp
// Usage on a minimal API endpoint:
var filters = EndpointFilterHelper.GetAuthEndpointFilters(appConfig.Http.Authorization);
app.MapGet("/api/data", handler).AddFilters(filters);
```

When `Authorization.Enabled` is `false`, the filter passes all requests through -- no overhead.

### Security Audit Middleware

Logs a structured audit event for every request regardless of outcome. Uses `Stopwatch` for accurate timing and writes to a `finally` block so the audit is recorded even when downstream middleware throws.

Output format:
```
Security audit: POST /api/agents responded 200 in 45ms | UserAgent=AgentHub/1.0 | IP=::1
```

### Identity Service (Stub)

`IdentityService` implements `IIdentityService` from Application.Common. The stub always returns "Development User" and grants no roles. Replace with your production identity provider (Azure Entra ID, Auth0, etc.) before deployment.

```csharp
// Interface contract:
public interface IIdentityService
{
    Task<string?> GetUserNameAsync(string userId);
    Task<bool> IsInRoleAsync(string userId, string role);
    Task<bool> AuthorizeAsync(string userId, string policyName);
    Task<Result<string>> CreateUserAsync(string userName, string password);
    Task<Result> DeleteUserAsync(string userId);
}
```

### Claim Extensions

Typed accessors for `ClaimsPrincipal` backed by claim constants from Domain.Common:

```csharp
var userId = principal.GetUserId();       // ClaimConstants.UserId
var isAdmin = principal.IsAdmin();        // ClaimConstants.IsAdmin → bool
var terms = principal.HasAgreedToTerms(); // ClaimConstants.AgreedToTerms → bool
```

## Project Structure

```
Infrastructure.Common/
├── Extensions/
│   └── ClaimExtensions.cs                  Typed claim accessors (GetUserId, IsAdmin, etc.)
├── Middleware/
│   ├── Cors/
│   │   └── DynamicCorsMiddleware.cs        Runtime-configurable origin allowlist
│   ├── EndpointFilters/
│   │   ├── HttpAuthEndpointFilter.cs       Dual API key validation (timing-safe)
│   │   └── HttpErrorEndpointFilter.cs      413 → Problem Details conversion
│   ├── ExceptionHandling/
│   │   └── GlobalExceptionMiddleware.cs    Exception → HTTP status code mapping
│   └── Security/
│       ├── SecurityAuditMiddleware.cs      Compliance-grade request audit logging
│       └── SecurityHeadersMiddleware.cs    Defense-in-depth response headers
├── Services/
│   └── IdentityService.cs                  Stub IIdentityService (replace for production)
└── DependencyInjection.cs                  AddInfrastructureCommonDependencies()
```

## Key Types Reference

| Type | Purpose | Used By |
|------|---------|---------|
| `DynamicCorsMiddleware` | Live-reloadable CORS origin enforcement | Presentation middleware pipeline |
| `SecurityHeadersMiddleware` | Response security headers | Presentation middleware pipeline |
| `SecurityAuditMiddleware` | Request audit logging | Presentation middleware pipeline |
| `GlobalExceptionMiddleware` | Exception → JSON error response | Presentation middleware pipeline |
| `HttpAuthEndpointFilter` | Constant-time API key validation | Minimal API endpoints (via AddFilters) |
| `HttpErrorEndpointFilter` | 413 → Problem Details | Minimal API endpoints (via AddFilters) |
| `IdentityService` | Stub identity provider | Authorization pipeline |
| `ClaimExtensions` | Typed claim accessors | `PermissionAuthHandler`, controllers |

## Configuration

This project reads from `AppConfig:Http` for CORS and authorization:

```json
{
  "AppConfig": {
    "Http": {
      "CorsAllowedOrigins": "http://localhost:5173;http://localhost:5174",
      "Authorization": {
        "Enabled": true,
        "HttpHeaderName": "X-API-Key",
        "AccessKey1": "-- set via dotnet user-secrets --",
        "AccessKey2": "-- for zero-downtime key rotation --"
      }
    }
  }
}
```

## How to Run

This is a class library -- it doesn't run independently. Its middleware is registered by Presentation hosts:

```csharp
// In Presentation.Common.Extensions.IApplicationBuilderExtensions:
app.UseMiddleware<SecurityAuditMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<DynamicCorsMiddleware>();
app.UseMiddleware<GlobalExceptionMiddleware>();
```

```bash
# Build
dotnet build src/Content/Infrastructure/Infrastructure.Common

# Run via host
dotnet run --project src/Content/Presentation/Presentation.AgentHub
```

## Common Tasks

### Adding a New Middleware

1. Create the class in the appropriate `Middleware/` subfolder
2. Inject `RequestDelegate _next` and required services via constructor
3. Implement `Task InvokeAsync(HttpContext context)`
4. Register in `Presentation.Common.Extensions.IApplicationBuilderExtensions` in correct pipeline order

### Replacing the Identity Service

1. Create a real implementation in Infrastructure.Common (or a new Infrastructure project)
2. Implement the full `IIdentityService` interface
3. Update `DependencyInjection.cs` to register your implementation instead of the stub

### Adding a New Exception Mapping

Add a new entry to `ExceptionStatusMap` in `GlobalExceptionMiddleware`:

```csharp
[typeof(ConflictException)] = StatusCodes.Status409Conflict,
```

## Dependencies

**Project References:**
- `Application.Common` -- `IIdentityService`, `Result<T>`, exception hierarchy (`BadRequestException`, etc.), `HttpAuthorizationConfig`

**NuGet Packages:**
- None (this project has zero external NuGet dependencies -- it uses only ASP.NET Core framework types)

## Testing

**Test project:** `Tests/Infrastructure.Common.Tests`

```bash
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~Infrastructure.Common"
```

**Coverage areas:**
- Middleware pipeline order and response headers
- Dynamic CORS origin matching (case-insensitive, empty origin, preflight)
- Exception-to-status mapping (all exception types + AggregateException unwrapping)
- API key validation (valid key, invalid key, missing header, disabled mode)
- Constant-time comparison correctness
- Claim extension edge cases (missing claims, empty values)
