using Presentation.BundleApi.Extensions;
using Presentation.Common.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Composition root — binds AppConfig and wires every Application + Infrastructure layer, including the bundle
// command/query handlers, the in-memory handle/job stores, the dispatch queue, and the background dispatcher
// + cleanup sweeper. This one call is what makes the bundle endpoints resolve end-to-end. HealthChecks UI is
// off: this is a lean, headless API host.
builder.Services.GetServices(includeHealthChecksUI: false);

// Host-specific wiring: controllers, the fail-closed authentication scheme (its own audience), authorization,
// and per-path rate limiting.
builder.Services.AddBundleApiServices(builder.Configuration);

// Enforce the harness's boot-time DI validation policy (ValidateScopes + ValidateOnBuild) in ALL environments,
// single-sourced so hosts cannot drift: it catches captive dependencies and turns a mis-wired handler into a
// caught-at-startup failure rather than a first-dispatch crash.
builder.Host.UseDefaultServiceProvider(
    Presentation.Common.Extensions.IServiceCollectionExtensions.ApplyValidationPolicy);

var app = builder.Build();

// Middleware pipeline — order is not negotiable. Security headers and exception handling run before routing
// so every response is covered; rate limiting runs after authentication so per-caller partitions can see the
// authenticated principal.
app.UseSecurityHeadersMiddleware();
app.UseGlobalExceptionMiddleware();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

app.Run();

/// <summary>
/// Exposes <c>Program</c> as a public partial class so <c>WebApplicationFactory&lt;Program&gt;</c> can host the
/// full bundle-API pipeline from the integration test project.
/// </summary>
public partial class Program { }
