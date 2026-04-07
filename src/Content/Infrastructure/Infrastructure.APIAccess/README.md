# Infrastructure.APIAccess

A comprehensive .NET infrastructure project that provides robust HTTP client management, service discovery, authentication, and request handling capabilities for the agentic harness.

## Overview

Infrastructure.APIAccess is designed to solve common challenges in building distributed applications, including:

- **Type-safe HTTP client configuration** with automatic endpoint resolution
- **Service discovery** driven by application configuration with health checking
- **Request tracing and correlation** across service boundaries
- **Resilient HTTP communication** with retry, timeout, and circuit breaker patterns
- **Permission-based authorization** using custom attributes
- **Structured logging and monitoring** for HTTP operations

## Architecture

```
Infrastructure.APIAccess/
├── Auth/                            # Authentication and authorization
│   ├── Attributes/                  # Custom authorization attributes
│   │   └── PermissionAuthorizeAttribute.cs  # Policy-based permission attribute
│   ├── Handlers/                    # Authorization handlers
│   │   └── PermissionAuthHandler.cs         # Evaluates permission requirements
│   └── Requirements/                # Authorization requirements
│       └── PermissionRequirement.cs         # IAuthorizationRequirement for permissions
├── Common/                          # Shared utilities and extensions
│   ├── ApiAccessConstants.cs        # Configuration section name constants
│   └── Extensions/
│       └── IServiceCollectionExtensions.cs  # Kestrel, CORS, Swagger, resilience, rate limiting
├── Handlers/                        # HTTP message handlers (delegating handler pipeline)
│   ├── CorrelationIdDelegatingHandler.cs    # Propagates correlation IDs across services
│   ├── DefaultHttpClientHandler.cs          # Base handler with compression and dev cert bypass
│   ├── LoggingDelegatingHandler.cs          # Debug-level HTTP request logging
│   └── UserAgentDelegatingHandler.cs        # Adds User-Agent from assembly metadata
├── Services/
│   └── ApiEndpointResolverService.cs        # Service discovery with endpoint health checking
└── DependencyInjection.cs           # Service registration entry point
```

## Quick Start

### 1. Registration

Register the Infrastructure.APIAccess services in your DI composition root:

```csharp
using Infrastructure.APIAccess;

var builder = WebApplication.CreateBuilder(args);

// Configure and bind AppConfig
builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("AppConfig"));

// Add Infrastructure.APIAccess services
builder.Services.AddInfrastructureApiAccessDependencies(appConfig);
```

### 2. Configuration

Add your HTTP and resilience configuration to `appsettings.json`:

```json
{
  "AppConfig": {
    "Http": {
      "CorsAllowedOrigins": "https://localhost:4200",
      "Policies": {
        "HttpTimeout": {
          "Timeout": "00:01:00"
        },
        "HttpRetry": {
          "Count": 3,
          "Delay": "00:00:01"
        },
        "HttpCircuitBreaker": {
          "FailureRatio": 0.1,
          "DurationOfBreak": "00:00:30"
        }
      },
      "HttpSwagger": {
        "OpenApiEnabled": true
      }
    }
  }
}
```

## Core Components

### DependencyInjection

The entry point for registering all API access services. Accepts `AppConfig` for configuration-driven setup:

```csharp
public static IServiceCollection AddInfrastructureApiAccessDependencies(
    this IServiceCollection services,
    AppConfig appConfig)
```

Registers:
- Correlation ID functionality for request tracing
- Memory cache for endpoint resolution caching
- `ApiEndpointResolverService` for typed client configuration
- Default HTTP client with standard delegating handlers

### ApiEndpointResolverService

Resolves API endpoints and retrieves strongly-typed HTTP client configurations from `AppConfig`. Features include:
- Cached endpoint resolution using `IMemoryCache`
- Service discovery with health checking across primary and alternative endpoints
- Configuration-driven cache duration per client

```csharp
public Uri ResolveEndpoint<TClientOptions>(string configurationSectionName)
    where TClientOptions : HttpClientConfig, new()

public TClientOptions GetClientConfig<TClientOptions>(string configurationSectionName)
    where TClientOptions : HttpClientConfig, new()
```

### IServiceCollectionExtensions

Provides extension methods for configuring the HTTP pipeline and server options:

| Method | Purpose |
|--------|---------|
| `AddCustomKestrelServerOptions` | Production-ready connection and request body limits |
| `AddCustomApiVersioning` | Header-based API versioning (`X-Api-Version`) |
| `AddCustomSwaggerGen` | OpenAPI generation with XML docs and security scheme |
| `AddCustomRateLimiter` | Fixed-window rate limiting for AI and MCP endpoints |
| `AddCustomCorsPolicy` | CORS policies for config, AI Copilot, and MCP Server |
| `AddResiliencePipelines` | Polly retry, timeout, circuit breaker, and rate limiter |
| `AddDefaultHttpClient` | Default HTTP client with delegating handler pipeline |
| `AddHttpClient<T,TImpl,TOpts>` | Typed HTTP client with endpoint resolution and resilience |

## HTTP Handlers

### CorrelationIdDelegatingHandler

Propagates correlation IDs to outgoing HTTP requests using the `CorrelationId` library. Essential for tracing requests through multiple service calls (Azure OpenAI, Content Safety, MCP servers).

### DefaultHttpClientHandler

Configurable primary HTTP handler providing:
- Automatic decompression (Brotli, Deflate, GZip)
- Development-environment certificate validation bypass
- Production-standard security settings

### LoggingDelegatingHandler

Logs all outgoing HTTP requests at Debug level for both synchronous and asynchronous paths. Debug-level logging avoids cluttering production logs.

### UserAgentDelegatingHandler

Sets a standard User-Agent header on all outgoing requests, built from assembly metadata (product name, version, OS). Supports custom values via constructor overloads.

## Authentication

### Permission-Based Authorization

Use the `PermissionAuthorizeAttribute` for fine-grained access control:

```csharp
[PermissionAuthorize(AuthPermissions.Admin)]
public class AdminController : ControllerBase
{
    // Only users with Admin permission can access
}

[PermissionAuthorize(AuthPermissions.TermsAgreement, AuthPermissions.Access)]
public class UserController : ControllerBase
{
    // Requires both TermsAgreement AND Access permissions
}
```

### Available Permissions

Defined in `AuthPermissions` enum (`Domain.Common.Enums`):

- **Access** (0) - Basic access permission
- **TermsAgreement** (1) - User has agreed to terms of service
- **Admin** (2) - Administrative access

See the `Auth/` subdirectory READMEs for detailed documentation on each component.

## Resilience Patterns

All HTTP clients are automatically configured with Polly resilience pipelines:

- **Retry** - Exponential backoff with jitter for network and strategy exceptions
- **Timeout** - Configurable via `AppConfig.Http.Policies.HttpTimeout`
- **Circuit Breaker** - Configurable failure ratio and break duration
- **Rate Limiter** - Sliding window (100 requests/minute, 4 segments)

Retryable exceptions include `SocketException`, `HttpRequestException`, `TimeoutRejectedException`, `BrokenCircuitException`, and `RateLimiterRejectedException`.

## Adding New HTTP Clients

1. **Add configuration class** extending `HttpClientConfig` in `Domain.Common.Config.Http`
2. **Add constant** in `ApiAccessConstants.cs` for the configuration section name
3. **Update `GetClientConfig`** in `ApiEndpointResolverService` to resolve the new config
4. **Create client interface** in `Application.Common/Interfaces/`
5. **Implement client** in `Infrastructure.APIAccess/`
6. **Register client** in `DependencyInjection.cs` using `AddHttpClient<T,TImpl,TOpts>`
7. **Add unit tests**

```csharp
// Registration example:
services.AddHttpClient<IMyApiClient, MyApiClient, MyApiClientConfig>(
    ApiAccessConstants.MY_API_CONFIG_SECTION);
```

## Error Handling

### Exception Types

- **ArgumentException**: Missing or invalid configuration
- **HttpRequestException**: Network or HTTP protocol errors
- **TimeoutRejectedException**: Request timeout (handled by resilience)
- **BrokenCircuitException**: Circuit breaker is open (handled by resilience)
- **RateLimiterRejectedException**: Rate limit exceeded (handled by resilience)

## Code Standards

- Follow C# coding conventions (PascalCase, `_camelCase` for private fields)
- Add XML documentation to all public APIs
- Write unit tests for new functionality
- Use async/await for I/O operations
- Include comprehensive error handling
- Use `ArgumentNullException.ThrowIfNull` for parameter validation
