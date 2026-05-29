using Presentation.AgentHub;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.Hubs;
using Presentation.Common.Extensions;

var builder = WebApplication.CreateBuilder(args);

// All logging is routed through the M.E.L. provider pipeline configured in
// Application.Common.ConfigureLogging: NamedPipeLoggerProvider streams to
// Presentation.LoggerUI, FileLoggerProvider + StructuredJsonLoggerProvider
// persist human-readable and ndjson output, and the execution-aware console
// formatter preserves the dev experience.
builder.Services.GetServices(includeHealthChecksUI: true);

// Register AgentHub-specific services (auth, SignalR, CORS, rate limiting, config).
builder.Services.AddAgentHubServices(builder.Configuration, builder.Environment);

// MemoizedPromptComposer (singleton) → IPromptSectionProvider (transient) → IAgentExecutionContext (scoped)
// creates a captive dependency that ASP.NET Core rejects by default. Scope validation is suppressed
// in Development only. Production runs with validation enabled to catch data leakage between requests.
if (builder.Environment.IsDevelopment())
{
    builder.Host.UseDefaultServiceProvider(options =>
    {
        options.ValidateScopes = false;
        options.ValidateOnBuild = false;
    });
}

var app = builder.Build();

// Middleware pipeline — order is not negotiable.
// Security headers and exception handling run before routing so every response is covered.
app.UseSecurityHeadersMiddleware();
app.UseGlobalExceptionMiddleware();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseHttpsRedirection();
// UseCors must precede UseAuthentication so CORS preflight (OPTIONS) is answered
// before the auth middleware can reject with 401.
app.UseRouting();
app.UseCors("AgentHubCors");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHub<AgentTelemetryHub>("/hubs/agent");
app.MapAgUiEndpoints();
app.MapPrometheusScrapingEndpoint().RequireAuthorization();
app.AddHealthCheckEndpoint("/api");

app.Run();

// Exposes Program as a public partial class so WebApplicationFactory<Program>
// can reference it from the integration test project.
public partial class Program { }
