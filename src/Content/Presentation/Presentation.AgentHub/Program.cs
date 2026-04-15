using Presentation.AgentHub;
using Presentation.AgentHub.Hubs;
using Presentation.Common.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register all shared layers (Application, Infrastructure, OTel, HealthChecks UI).
builder.Services.GetServices(includeHealthChecksUI: true);

// Register AgentHub-specific services (auth, SignalR, CORS, rate limiting, config).
builder.Services.AddAgentHubServices(builder.Configuration);

builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(5001));

var app = builder.Build();

// Middleware pipeline — order is not negotiable.
// UseCors must precede UseAuthentication so CORS preflight (OPTIONS) is answered
// before the auth middleware can reject with 401.
app.UseRouting();
app.UseCors("AgentHubCors");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHub<AgentTelemetryHub>("/hubs/agent");
app.AddHealthCheckEndpoint("/api");

app.Run();

// Exposes Program as a public partial class so WebApplicationFactory<Program>
// can reference it from the integration test project.
public partial class Program { }
