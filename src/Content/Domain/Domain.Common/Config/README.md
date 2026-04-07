# Configuration System

This directory contains all configuration classes for the agentic harness. Configuration is organized by functional area and follows a hierarchical structure matching the appsettings.json schema.

## Root Configuration

The `AppConfig` class is the root configuration object that aggregates all section configurations:

```csharp
public class AppConfig
{
    public CommonConfig Common { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public AgentConfig Agent { get; set; } = new();
    public HttpConfig Http { get; set; } = new();
    public InfrastructureConfig Infrastructure { get; set; } = new();
}
```

Mutable setters are required by `IOptionsMonitor<T>` binding. Treat instances as read-only after DI setup.

## Directory Structure

```
Config/
├── Http/                              # HTTP-related configuration
│   ├── OpenApi/                       # OpenAPI/Swagger specification settings
│   │   ├── HttpOpenApiSpecConfig.cs   # Spec name, version, info
│   │   ├── HttpOpenApiInfoConfig.cs   # Title, description, terms of service
│   │   ├── HttpOpenApiContactConfig.cs       # Contact name and email
│   │   ├── HttpOpenApiLicenseConfig.cs       # License name and URL
│   │   └── HttpOpenApiSecuritySchemeConfig.cs # Security scheme definition
│   ├── Policies/                      # Resilience policy settings
│   │   ├── HttpPolicyConfig.cs        # Aggregates retry, timeout, circuit breaker
│   │   ├── HttpRetryPolicyConfig.cs   # Retry count and delay
│   │   ├── HttpTimeoutPolicyConfig.cs # Request timeout duration
│   │   └── HttpCircuitBreakerPolicyConfig.cs # Failure ratio and break duration
│   ├── HttpAuthorizationConfig.cs     # Authorization toggle settings
│   ├── HttpClientConfig.cs            # Base config for typed HTTP clients
│   ├── HttpConfig.cs                  # Root HTTP config (CORS, maintenance, policies)
│   └── HttpSwaggerConfig.cs           # Swagger enable/service auth toggle
├── Infrastructure/                    # Infrastructure services configuration
│   ├── ContentProvider/
│   │   └── ContentProviderConfigTypes.cs  # Content provider type enum
│   ├── ContentProviderConfig.cs       # Content provider base path and type
│   ├── InfrastructureConfig.cs        # Root infra config (state, content provider)
│   └── StateManagementConfig.cs       # State persistence settings
└── AppConfig.cs                       # Root: Common, Logging, Agent, Http, Infrastructure
                                       # (includes inline AgentConfig, CommonConfig, LoggingConfig)
```

## Usage with IOptionsMonitor

Configuration is accessed through dependency injection using `IOptionsMonitor<AppConfig>`:

```csharp
public class MyService
{
    private readonly IOptionsMonitor<AppConfig> _config;

    public MyService(IOptionsMonitor<AppConfig> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    public void DoWork()
    {
        // Access current configuration (supports hot reload)
        var timeout = _config.CurrentValue.Http.Policies.HttpTimeout.Timeout;

        // Access nested configuration
        var corsOrigins = _config.CurrentValue.Http.CorsAllowedOrigins;
    }
}
```

## Key Configuration Sections

### Common Configuration (`AppConfig.Common`)

- **SlowThresholdSec**: Threshold beyond which requests are considered slow (default: 5s). Used by `RequestPerformanceBehavior`.

### Logging Configuration (`AppConfig.Logging`)

- **LogsBasePath**: Base directory for file-based log output
- **PipeName**: Named pipe for real-time log streaming (default: "agentic-harness-logs")
- **EnableStructuredJson**: Toggle for JSONL structured logging (default: true)
- **RingBufferCapacity**: In-memory ring buffer size for diagnostics endpoints (default: 500)

### Agent Configuration (`AppConfig.Agent`)

- **DefaultRequestTimeoutSec**: Default MediatR request timeout (default: 30s)
- **DefaultTokenBudget**: Default token budget per agent session (default: 128,000)

### HTTP Configuration (`AppConfig.Http`)

- **CorsAllowedOrigins**: Semicolon-separated allowed CORS origins
- **Policies**: Resilience policies (retry, timeout, circuit breaker)
- **HttpSwagger**: OpenAPI/Swagger specification settings
- **Authorization**: Authorization toggle and configuration
- **MaintenanceMode**: Maintenance mode toggle

### Infrastructure Configuration (`AppConfig.Infrastructure`)

- **StateManagement**: State persistence settings
- **ContentProvider**: Content provider base path and type

## Coding Conventions

1. **One class per file** - Each configuration class has its own file
2. **Namespace matches folder path** - `Config/Http/Policies/` maps to `Domain.Common.Config.Http.Policies`
3. **XML documentation** - All public properties have XML doc comments
4. **Default values** - Properties have sensible defaults
5. **Validation attributes** - Use `[Required]`, `[Range]`, `[NotEmptyOrWhitespace]` where appropriate

## Configuration Binding

Configuration is typically bound in a DI extension method:

```csharp
services.Configure<AppConfig>(configuration.GetSection("AppConfig"));
```

Or with validation:

```csharp
services.AddOptions<AppConfig>()
    .Bind(configuration.GetSection("AppConfig"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

## Extending Configuration

As the harness expands, new configuration sections will be added for:
- AI service settings (Azure OpenAI, agent framework, content safety)
- Database connections (Azure SQL, Redis, Blob Storage)
- Observability (Application Insights, OpenTelemetry)
- External system integrations

To add a new section:

1. Create a configuration class in the appropriate subfolder
2. Add a property to the parent config (or `AppConfig` for top-level sections)
3. Initialize with `= new()` for non-nullable reference types
4. Add XML documentation explaining each property's purpose
5. Update appsettings.json with matching structure
