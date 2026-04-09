# Infrastructure.Common

The shared infrastructure that every HTTP endpoint in the harness depends on. This project handles the concerns that sit between the application logic and the outside world: CORS policies, security headers, exception-to-HTTP mapping, API key validation, and request auditing.

It's small by design — only the truly cross-cutting HTTP infrastructure belongs here. Domain-specific infrastructure (AI, MCP, connectors) lives in its own project.

---

## Security Middleware

Three middleware components form the security baseline for every request:

**SecurityHeadersMiddleware** applies defense-in-depth headers to all responses: `Content-Security-Policy`, `Strict-Transport-Security`, `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, and more. These are the headers that prevent clickjacking, MIME sniffing, and other common web attacks.

**SecurityAuditMiddleware** logs security-relevant metadata for every request: HTTP method, path, response status, duration, user agent, and client IP. This isn't application logging — it's compliance-grade audit data for incident analysis.

**DynamicCorsMiddleware** reads allowed origins from `AppConfig` on every request, enabling runtime CORS reconfiguration without restarting the application. No wildcards in production — explicit allowlists only.

## Endpoint Filters

**HttpAuthEndpointFilter** validates incoming requests against dual API keys (`AccessKey1`/`AccessKey2`) using constant-time comparison to prevent timing attacks. Supporting two keys enables zero-downtime key rotation — deploy the new key as `AccessKey2`, update clients, then retire `AccessKey1`.

**HttpErrorEndpointFilter** catches `BadHttpRequestException` (typically payload-too-large) and converts it to a structured RFC 7807 Problem Details response instead of letting ASP.NET Core return a bare 400.

## Exception Handling

**GlobalExceptionMiddleware** is the outermost safety net. It maps the domain exception hierarchy to HTTP status codes: `BadRequestException` → 400, `ForbiddenAccessException` → 403, `EntityNotFoundException` → 404, and so on. In development, stack traces are included. In production, they're hidden — error responses never leak internal paths or implementation details.

## Identity

**IdentityService** is a stub implementation of `IIdentityService`. It provides the contract surface that the authorization pipeline depends on. Replace it with Azure Entra ID, Auth0, or your identity provider of choice before production.

**ClaimExtensions** provides typed accessors for `ClaimsPrincipal`: `GetUserId()`, `IsAdmin()`, `HasAgreedToTerms()` — all backed by the claim type constants defined in Domain.Common.

---

## Project Structure

```
Infrastructure.Common/
├── Extensions/
│   └── ClaimExtensions.cs            Typed claim accessors
├── Middleware/
│   ├── Cors/
│   │   └── DynamicCorsMiddleware.cs  Runtime-configurable CORS
│   ├── EndpointFilters/
│   │   ├── HttpAuthEndpointFilter.cs API key validation (timing-safe)
│   │   └── HttpErrorEndpointFilter.cs Payload error → Problem Details
│   ├── ExceptionHandling/
│   │   └── GlobalExceptionMiddleware.cs Exception → HTTP status mapping
│   └── Security/
│       ├── SecurityAuditMiddleware.cs  Compliance request logging
│       └── SecurityHeadersMiddleware.cs Defense-in-depth headers
├── Services/
│   └── IdentityService.cs            Stub identity provider
└── DependencyInjection.cs
```

## Dependencies

- **Application.Common** — `IIdentityService`, `IUser`, exception hierarchy, config types
