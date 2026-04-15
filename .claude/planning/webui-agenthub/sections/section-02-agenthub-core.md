# Section 02: Presentation.AgentHub — Core Setup

## Overview

This section wires the `Program.cs`, `DependencyInjection.cs`, and `appsettings.json` for `Presentation.AgentHub`. After completing this section the project must build, start, and return 401 on all endpoints (auth is wired, no routes yet). Routes, the SignalR hub, conversation store, and OTel bridge are added in later sections — this section only establishes the hosting and service registration skeleton.

**Depends on:** section-01-scaffolding (project files must exist and build)

**Blocks:** section-03-conversation-store, section-05-otel-bridge, section-06-mcp-api (all depend on the DI container established here)

**Can parallelize with:** section-08-webui-shell

---

## Tests First

These tests live in `src/Content/Tests/Presentation.AgentHub.Tests/`. Write them before implementing. They use `TestWebApplicationFactory` and `TestAuthHandler` scaffolded in section-07, but their stubs should exist now so these tests can compile. For now, stub `TestWebApplicationFactory` as a class inheriting `WebApplicationFactory<Program>` with no overrides — section-07 will flesh it out.

**Verify command:** `dotnet test src/AgenticHarness.slnx`

```
// CoreSetupTests.cs

[Fact] GET_WithoutToken_Returns401()
[Fact] GET_WithValidTestToken_Returns200()   // uses TestAuthHandler
[Fact] Options_CorsPreflightFromLocalhost5173_ReturnsAllowedHeaders()
[Fact] UseCors_BeforeUseAuthentication_PreflightDoesNotReturn401()
[Fact] SignalRUpgrade_WithoutToken_IsRejected()
[Fact] McpInvoke_Called11TimesRapidly_Returns429OnEleventh()  // rate limit
```

The CORS test must verify that `OPTIONS /api/agents` with `Origin: http://localhost:5173` and `Access-Control-Request-Method: GET` receives a 2xx (not 401) — proving CORS is handled before auth middleware.

The rate limiting test sends 11 rapid `POST /api/mcp/tools/any/invoke` requests and asserts the 11th returns 429. The route doesn't need to exist yet — the rate limiter middleware acts on the path pattern before the route resolves.

---

## Files to Create

```
src/Content/Presentation/Presentation.AgentHub/
  Program.cs
  DependencyInjection.cs
  appsettings.json
  appsettings.Development.json
  Models/
    AgentHubConfig.cs
    AgentHubCorsConfig.cs
```

---

## Program.cs

Minimal top-level file. Responsibilities in this exact order:

1. `services.GetServices(includeHealthChecksUI: true)` — from `Presentation.Common`, registers the entire agent stack (Application → Infrastructure → Observability)
2. `services.AddAgentHubServices(builder.Configuration)` — the local extension (see DependencyInjection.cs below)
3. Configure Kestrel to listen on port 5001
4. Build the app
5. Middleware pipeline in this order — order is not negotiable:
   - `UseRouting()`
   - `UseCors("AgentHubCors")`
   - `UseAuthentication()`
   - `UseAuthorization()`
   - `UseRateLimiter()`
   - `MapControllers()`
   - `MapHub<AgentTelemetryHub>("/hubs/agent")` — stub class, added in section-04
   - Health check endpoints

`UseCors` must come after `UseRouting` and before `UseAuthentication`. CORS preflight requests (`OPTIONS`) must be answered before the auth middleware can reject them with 401.

---

## DependencyInjection.cs

Single public static extension method: `AddAgentHubServices(this IServiceCollection services, IConfiguration configuration)`.

### Azure AD Auth

Call `services.AddMicrosoftIdentityWebApiAuthentication(configuration)`. This binds the `AzureAd` config section by convention.

After that call, retrieve the `JwtBearerOptions` and set `Events.OnMessageReceived` to extract the `access_token` query parameter when the request path starts with `/hubs`:

```csharp
// Stub — exact lambda body matters
options.Events = new JwtBearerEvents
{
    OnMessageReceived = context =>
    {
        var accessToken = context.Request.Query["access_token"];
        var path = context.HttpContext.Request.Path;
        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
        {
            context.Token = accessToken;
        }
        return Task.CompletedTask;
    }
};
```

This is required because SignalR WebSocket upgrade requests cannot carry an `Authorization` header — the client sends the token as a query parameter instead. Without this handler, all WebSocket connections fail auth.

### SignalR

```csharp
services.AddSignalR(opts => opts.CloseOnAuthenticationExpiration = true);
```

No custom serialization. Default `System.Text.Json` is sufficient for the POC.

### Rate Limiting

```csharp
services.AddRateLimiter(options =>
{
    // Fixed window: 10 req/min per IP on MCP tool invoke endpoint
    options.AddFixedWindowLimiter("McpToolInvoke", o => { ... });

    // Token bucket: 10 SendMessage calls/min per connection on hub
    options.AddTokenBucketLimiter("HubSendMessage", o => { ... });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
```

The `McpToolInvoke` policy is applied via `[EnableRateLimiting("McpToolInvoke")]` attribute on the controller action (section-06). The `HubSendMessage` policy is applied in section-04. Register `app.UseRateLimiter()` in `Program.cs` between `UseAuthorization()` and `MapControllers()`.

### CORS

```csharp
services.AddCors(options =>
{
    options.AddPolicy("AgentHubCors", policy =>
    {
        var allowedOrigins = configuration
            .GetSection("AppConfig:AgentHub:Cors:AllowedOrigins")
            .Get<string[]>() ?? [];

        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader();
        // Do NOT call AllowCredentials() — Bearer token auth does not use cookies.
        // Enabling credentials mode unnecessarily restricts allowed origins.
    });
});
```

In `appsettings.Development.json`, always include `http://localhost:5173` in `AllowedOrigins`.

### AgentHubConfig

```csharp
services.Configure<AgentHubConfig>(
    configuration.GetSection("AppConfig:AgentHub"));
```

`AgentHubConfig` is an `init`-only record:

```csharp
/// <summary>Configuration for the AgentHub presentation host.</summary>
public sealed record AgentHubConfig
{
    public string ConversationsPath { get; init; } = "./conversations";
    public string DefaultAgentName { get; init; } = string.Empty;
    public int MaxHistoryMessages { get; init; } = 20;
    public AgentHubCorsConfig Cors { get; init; } = new();
}

/// <summary>CORS sub-configuration for AgentHub.</summary>
public sealed record AgentHubCorsConfig
{
    public string[] AllowedOrigins { get; init; } = [];
}
```

### Deferred Registrations (stubs now, implemented later)

Add these lines now so the container is consistent when later sections add the implementations:

```csharp
// Section 3 — FileSystemConversationStore
// services.AddSingleton<IConversationStore, FileSystemConversationStore>();

// Section 5 — SignalRSpanExporter
// services.AddSingleton<SignalRSpanExporter>();
// services.AddHostedService(sp => sp.GetRequiredService<SignalRSpanExporter>());
```

Leave them commented out with section references. Uncomment as each section is implemented to avoid build errors from missing types.

---

## appsettings.json Shape

```json
{
  "AzureAd": {
    "TenantId": "PLACEHOLDER — set via user-secrets in development",
    "ClientId": "PLACEHOLDER — set via user-secrets in development",
    "Audience": "PLACEHOLDER — api://{apiClientId}"
  },
  "AppConfig": {
    "AgentHub": {
      "ConversationsPath": "./conversations",
      "DefaultAgentName": "",
      "MaxHistoryMessages": 20,
      "Cors": {
        "AllowedOrigins": []
      }
    }
  }
}
```

`AzureAd` lives at root level (not under `AppConfig`) because `Microsoft.Identity.Web` binds from `configuration.GetSection("AzureAd")` by convention.

`appsettings.Development.json` overrides:

```json
{
  "AzureAd": {
    "TenantId": "",
    "ClientId": "",
    "Audience": ""
  },
  "AppConfig": {
    "AgentHub": {
      "Cors": {
        "AllowedOrigins": [ "http://localhost:5173" ]
      }
    }
  }
}
```

Actual TenantId/ClientId values go in `dotnet user-secrets`, never in committed files.

---

## XML Documentation

All public types (`AgentHubConfig`, `AgentHubCorsConfig`, the `AddAgentHubServices` extension method) require full XML doc comments. This is a template codebase — docs are teaching material for consumers.

Example:

```csharp
/// <summary>
/// Registers all AgentHub-specific services: Azure AD authentication with
/// SignalR token extraction, SignalR hub, CORS, rate limiting, and config binding.
/// Call this after <see cref="Presentation.Common.ServiceCollectionExtensions.GetServices"/>.
/// </summary>
/// <param name="services">The service collection to extend.</param>
/// <param name="configuration">The application configuration root.</param>
public static IServiceCollection AddAgentHubServices(
    this IServiceCollection services,
    IConfiguration configuration)
```

---

## Verification

After this section is complete:

```bash
dotnet build src/AgenticHarness.slnx
dotnet test src/AgenticHarness.slnx --filter "Category=CoreSetup"
```

Expected: build succeeds with 0 warnings, the 6 core-setup tests pass (or are skipped with a pending marker if `TestAuthHandler` isn't fleshed out yet), and the project starts with `dotnet run` and returns 401 on `GET /api/agents`.

---

## Implementation Notes (Actual)

### Deviations from Plan

- **`AddSignalR` options**: `CloseOnAuthenticationExpiration` does not exist on `HubOptions` in .NET 10. `AddSignalR()` is called with no options; section-04 will revisit if needed.
- **Rate limiting strategy**: `GlobalLimiter` (path-based, before routing resolves) is used for `POST /api/mcp/tools/*` instead of a named policy. This is required for the section-02 rate limit test to work before the actual MCP route exists (section-06). The GlobalLimiter approach also means no `[EnableRateLimiting]` attribute is needed on the future controller action.
- **`PostConfigure` event chaining**: After code review, `OnMessageReceived` is chained onto the existing `Events` object (rather than replacing it) to preserve `Microsoft.Identity.Web`'s `OnTokenValidated` / `OnAuthenticationFailed` handlers.
- **Test infrastructure additions**: Three test classes were created (not in the original plan):
  - `TestJwtBearerHandler` — no-op JWT stub (returns `NoResult()`); eliminates the need for valid AzureAd config in tests and lets UseAuthorization challenge with 401 for `[Authorize]` endpoints.
  - `TestWebApplicationFactory` — overrides `ConfigureWebHost` (sets CWD + Development env + installs TestJwtBearerHandler as default scheme) and `CreateHost` (suppresses scope validation to work around pre-existing captive dependency in shared DI).
  - `TestAuthHandler` — authenticates all requests as a fixed test user; used by `GET_WithValidTestToken_Returns200`.
- **Pre-existing scope violation**: `MemoizedPromptComposer` (singleton) → `IPromptSectionProvider` (transient) → `IAgentExecutionContext` (scoped) exists in the shared DI. Suppressed in tests via `ValidateScopes=false`; tracked as upstream backlog item.

### Actual Files Created/Modified

- `Presentation.AgentHub/DependencyInjection.cs` — `AddAgentHubServices` extension
- `Presentation.AgentHub/Program.cs` — full wiring (replaces stub)
- `Presentation.AgentHub/appsettings.json`
- `Presentation.AgentHub/appsettings.Development.json` — CORS origin + empty AzureAd overrides
- `Presentation.AgentHub/Models/AgentHubConfig.cs`
- `Presentation.AgentHub/Models/AgentHubCorsConfig.cs`
- `Presentation.AgentHub/Hubs/AgentTelemetryHub.cs` — stub with `[Authorize]`
- `Presentation.AgentHub/Controllers/AgentsController.cs` — stub with `[Authorize]`
- `Presentation.AgentHub.Tests/TestWebApplicationFactory.cs`
- `Presentation.AgentHub.Tests/TestAuthHandler.cs`
- `Presentation.AgentHub.Tests/TestJwtBearerHandler.cs`
- `Presentation.AgentHub.Tests/CoreSetupTests.cs`

### Test Results

- `dotnet test --filter "Category=CoreSetup"`: **6/6 passed**
- `dotnet test` (full suite): **0 failures, 0 regressions**
