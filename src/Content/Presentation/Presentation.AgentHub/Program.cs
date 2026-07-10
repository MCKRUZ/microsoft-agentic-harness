using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Presentation.AgentHub;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.HealthChecks;
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

// Enforce the harness's boot-time DI validation policy (ValidateScopes + ValidateOnBuild,
// audit item H2) in ALL environments. Single-sourced in IServiceCollectionExtensions so the
// web host and the console-style hosts can never drift: ValidateScopes catches captive
// dependencies (a singleton capturing per-request scoped state), and ValidateOnBuild turns the
// audit's "inert machinery" class — a globally-scanned MediatR handler whose dependency only
// one host supplies — from a silent-until-dispatched runtime crash into a caught-at-startup
// error. AgentHub's real AG-UI notifiers still win over the composition root's No-op defaults
// via last-registration-wins.
builder.Host.UseDefaultServiceProvider(
    Presentation.Common.Extensions.IServiceCollectionExtensions.ApplyValidationPolicy);

var app = builder.Build();

// Middleware pipeline — order is not negotiable.
// Security headers and exception handling run before routing so every response is covered.
app.UseSecurityHeadersMiddleware();
app.UseGlobalExceptionMiddleware();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    // Skipped in Development: the Vite dev proxy forwards over http://:52000, and an
    // http→https redirect (to :52001) makes the browser follow cross-origin, breaking
    // the same-origin proxy model and failing credentialed SignalR negotiation with CORS.
    app.UseHttpsRedirection();
}
// UseCors must precede UseAuthentication so CORS preflight (OPTIONS) is answered
// before the auth middleware can reject with 401.
app.UseRouting();
app.UseCors("AgentHubCors");
app.UseAuthentication();
app.UseAuthorization();
// Establish the per-request knowledge scope (user/tenant) from the authenticated principal,
// after auth so HttpContext.User is populated. Covers controllers + the AG-UI endpoint.
app.UseMiddleware<Presentation.AgentHub.Middleware.KnowledgeScopeMiddleware>();
app.UseRateLimiter();
app.MapControllers();
app.MapHub<AgentTelemetryHub>("/hubs/agent");
app.MapAgUiEndpoints();
app.MapPrometheusScrapingEndpoint().RequireAuthorization();
app.AddHealthCheckEndpoint("/api");

// Lightweight, AI-only readiness probe. Returns the missing configuration keys in its JSON body
// so a developer (or CI) can see at a glance why agent turns are failing.
app.MapHealthChecks("/health/ai", new HealthCheckOptions
{
    Predicate = static registration => registration.Tags.Contains("ai"),
    ResponseWriter = AiHealthEndpoint.WriteResponse,
});

app.Run();

// Exposes Program as a public partial class so WebApplicationFactory<Program>
// can reference it from the integration test project.
public partial class Program { }
