diff --git a/src/Content/Presentation/Presentation.AgentHub/Controllers/AgentsController.cs b/src/Content/Presentation/Presentation.AgentHub/Controllers/AgentsController.cs
new file mode 100644
index 0000000..501b57c
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Controllers/AgentsController.cs
@@ -0,0 +1,18 @@
+using Microsoft.AspNetCore.Authorization;
+using Microsoft.AspNetCore.Mvc;
+
+namespace Presentation.AgentHub.Controllers;
+
+/// <summary>
+/// REST controller for agent resource management.
+/// Stub — conversation store integration added in section 03.
+/// </summary>
+[ApiController]
+[Route("api/[controller]")]
+[Authorize]
+public class AgentsController : ControllerBase
+{
+    /// <summary>Returns the list of available agents. Requires authentication.</summary>
+    [HttpGet]
+    public IActionResult Get() => Ok(Array.Empty<object>());
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs b/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
new file mode 100644
index 0000000..a2d9899
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
@@ -0,0 +1,118 @@
+using Microsoft.AspNetCore.Authentication.JwtBearer;
+using Microsoft.AspNetCore.Http;
+using Microsoft.AspNetCore.RateLimiting;
+using Microsoft.Identity.Web;
+using Presentation.AgentHub.Models;
+using System.Threading.RateLimiting;
+
+namespace Presentation.AgentHub;
+
+/// <summary>
+/// Extension methods for registering AgentHub-specific services.
+/// </summary>
+public static class DependencyInjection
+{
+    /// <summary>
+    /// Registers all AgentHub-specific services: Azure AD authentication with
+    /// SignalR token extraction, SignalR hub, CORS, rate limiting, and config binding.
+    /// Call this after <see cref="Presentation.Common.Extensions.IServiceCollectionExtensions.GetServices"/>.
+    /// </summary>
+    /// <param name="services">The service collection to extend.</param>
+    /// <param name="configuration">The application configuration root.</param>
+    /// <returns>The service collection for chaining.</returns>
+    public static IServiceCollection AddAgentHubServices(
+        this IServiceCollection services,
+        IConfiguration configuration)
+    {
+        services.AddControllers();
+
+        services.AddMicrosoftIdentityWebApiAuthentication(configuration);
+
+        // SignalR WebSocket upgrades cannot carry an Authorization header.
+        // The client sends the bearer token as the `access_token` query parameter.
+        // PostConfigure ensures this runs after Microsoft.Identity.Web's configuration.
+        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
+        {
+            options.Events = new JwtBearerEvents
+            {
+                OnMessageReceived = context =>
+                {
+                    var accessToken = context.Request.Query["access_token"];
+                    var path = context.HttpContext.Request.Path;
+                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
+                    {
+                        context.Token = accessToken;
+                    }
+                    return Task.CompletedTask;
+                }
+            };
+        });
+
+        services.AddAuthorization();
+
+        services.AddSignalR();
+
+        services.AddRateLimiter(options =>
+        {
+            // Global limiter runs before routing resolves — enforces MCP tool invoke
+            // rate limit on the path pattern even before the route handler exists.
+            // Applied at 10 POST requests/min per IP on /api/mcp/tools/*.
+            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
+            {
+                if (context.Request.Method == HttpMethods.Post &&
+                    context.Request.Path.StartsWithSegments("/api/mcp/tools"))
+                {
+                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
+                    return RateLimitPartition.GetFixedWindowLimiter($"mcp:{ip}", _ => new FixedWindowRateLimiterOptions
+                    {
+                        PermitLimit = 10,
+                        Window = TimeSpan.FromMinutes(1),
+                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
+                        QueueLimit = 0,
+                    });
+                }
+                return RateLimitPartition.GetNoLimiter("none");
+            });
+
+            // Token bucket for SignalR hub send messages — applied in section-04
+            // via [EnableRateLimiting("HubSendMessage")] on the hub method.
+            options.AddTokenBucketLimiter("HubSendMessage", o =>
+            {
+                o.TokenLimit = 10;
+                o.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
+                o.TokensPerPeriod = 10;
+                o.AutoReplenishment = true;
+            });
+
+            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
+        });
+
+        services.AddCors(options =>
+        {
+            options.AddPolicy("AgentHubCors", policy =>
+            {
+                var allowedOrigins = configuration
+                    .GetSection("AppConfig:AgentHub:Cors:AllowedOrigins")
+                    .Get<string[]>() ?? [];
+
+                policy.WithOrigins(allowedOrigins)
+                      .AllowAnyMethod()
+                      .AllowAnyHeader();
+                // AllowCredentials() is intentionally omitted — Bearer token auth does not
+                // use cookies, and enabling it unnecessarily restricts allowed origins.
+            });
+        });
+
+        services.Configure<AgentHubConfig>(
+            configuration.GetSection("AppConfig:AgentHub"));
+
+        // Section 3 — FileSystemConversationStore
+        // services.AddSingleton<IConversationStore, FileSystemConversationStore>();
+
+        // Section 5 — SignalRSpanExporter
+        // services.AddSingleton<SignalRSpanExporter>();
+        // services.AddHostedService(sp => sp.GetRequiredService<SignalRSpanExporter>());
+
+        return services;
+    }
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Hubs/AgentTelemetryHub.cs b/src/Content/Presentation/Presentation.AgentHub/Hubs/AgentTelemetryHub.cs
new file mode 100644
index 0000000..a60f10c
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Hubs/AgentTelemetryHub.cs
@@ -0,0 +1,13 @@
+using Microsoft.AspNetCore.Authorization;
+using Microsoft.AspNetCore.SignalR;
+
+namespace Presentation.AgentHub.Hubs;
+
+/// <summary>
+/// SignalR hub that streams agent telemetry to connected clients.
+/// Stub implementation — full wiring added in section 04.
+/// </summary>
+[Authorize]
+public class AgentTelemetryHub : Hub
+{
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/AgentHubConfig.cs b/src/Content/Presentation/Presentation.AgentHub/Models/AgentHubConfig.cs
new file mode 100644
index 0000000..67f52e8
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/AgentHubConfig.cs
@@ -0,0 +1,20 @@
+namespace Presentation.AgentHub.Models;
+
+/// <summary>
+/// Configuration for the AgentHub presentation host.
+/// Bound from <c>AppConfig:AgentHub</c> in appsettings.json.
+/// </summary>
+public sealed record AgentHubConfig
+{
+    /// <summary>File system path where conversation records are persisted.</summary>
+    public string ConversationsPath { get; init; } = "./conversations";
+
+    /// <summary>Name of the default agent used when no agent is specified.</summary>
+    public string DefaultAgentName { get; init; } = string.Empty;
+
+    /// <summary>Maximum number of conversation messages dispatched to the agent per turn.</summary>
+    public int MaxHistoryMessages { get; init; } = 20;
+
+    /// <summary>CORS configuration for this host.</summary>
+    public AgentHubCorsConfig Cors { get; init; } = new();
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/AgentHubCorsConfig.cs b/src/Content/Presentation/Presentation.AgentHub/Models/AgentHubCorsConfig.cs
new file mode 100644
index 0000000..9a72a1a
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/AgentHubCorsConfig.cs
@@ -0,0 +1,14 @@
+namespace Presentation.AgentHub.Models;
+
+/// <summary>
+/// CORS sub-configuration for the AgentHub host.
+/// Bound from <c>AppConfig:AgentHub:Cors</c> in appsettings.json.
+/// </summary>
+public sealed record AgentHubCorsConfig
+{
+    /// <summary>
+    /// Origins allowed to make cross-origin requests to this host.
+    /// In development, always include <c>http://localhost:5173</c>.
+    /// </summary>
+    public string[] AllowedOrigins { get; init; } = [];
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Program.cs b/src/Content/Presentation/Presentation.AgentHub/Program.cs
index 0becd19..8dc92d0 100644
--- a/src/Content/Presentation/Presentation.AgentHub/Program.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/Program.cs
@@ -1,3 +1,33 @@
+using Presentation.AgentHub;
+using Presentation.AgentHub.Hubs;
+using Presentation.Common.Extensions;
+
 var builder = WebApplication.CreateBuilder(args);
+
+// Register all shared layers (Application, Infrastructure, OTel, HealthChecks UI).
+builder.Services.GetServices(includeHealthChecksUI: true);
+
+// Register AgentHub-specific services (auth, SignalR, CORS, rate limiting, config).
+builder.Services.AddAgentHubServices(builder.Configuration);
+
+builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(5001));
+
 var app = builder.Build();
+
+// Middleware pipeline — order is not negotiable.
+// UseCors must precede UseAuthentication so CORS preflight (OPTIONS) is answered
+// before the auth middleware can reject with 401.
+app.UseRouting();
+app.UseCors("AgentHubCors");
+app.UseAuthentication();
+app.UseAuthorization();
+app.UseRateLimiter();
+app.MapControllers();
+app.MapHub<AgentTelemetryHub>("/hubs/agent");
+app.AddHealthCheckEndpoint("/api");
+
 app.Run();
+
+// Exposes Program as a public partial class so WebApplicationFactory<Program>
+// can reference it from the integration test project.
+public partial class Program { }
diff --git a/src/Content/Presentation/Presentation.AgentHub/appsettings.Development.json b/src/Content/Presentation/Presentation.AgentHub/appsettings.Development.json
new file mode 100644
index 0000000..f367271
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/appsettings.Development.json
@@ -0,0 +1,14 @@
+{
+  "AzureAd": {
+    "TenantId": "",
+    "ClientId": "",
+    "Audience": ""
+  },
+  "AppConfig": {
+    "AgentHub": {
+      "Cors": {
+        "AllowedOrigins": [ "http://localhost:5173" ]
+      }
+    }
+  }
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/appsettings.json b/src/Content/Presentation/Presentation.AgentHub/appsettings.json
new file mode 100644
index 0000000..15b9d50
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/appsettings.json
@@ -0,0 +1,17 @@
+{
+  "AzureAd": {
+    "TenantId": "PLACEHOLDER — set via user-secrets in development",
+    "ClientId": "PLACEHOLDER — set via user-secrets in development",
+    "Audience": "PLACEHOLDER — api://{apiClientId}"
+  },
+  "AppConfig": {
+    "AgentHub": {
+      "ConversationsPath": "./conversations",
+      "DefaultAgentName": "",
+      "MaxHistoryMessages": 20,
+      "Cors": {
+        "AllowedOrigins": []
+      }
+    }
+  }
+}
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/CoreSetupTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/CoreSetupTests.cs
new file mode 100644
index 0000000..cbf14aa
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/CoreSetupTests.cs
@@ -0,0 +1,125 @@
+using Microsoft.AspNetCore.Authentication;
+using Microsoft.AspNetCore.Mvc.Testing;
+using Microsoft.AspNetCore.TestHost;
+using Microsoft.Extensions.DependencyInjection;
+using System.Net;
+using Xunit;
+
+namespace Presentation.AgentHub.Tests;
+
+/// <summary>
+/// Integration tests verifying that Presentation.AgentHub wires authentication,
+/// CORS, SignalR, and rate limiting correctly after section-02.
+/// </summary>
+[Trait("Category", "CoreSetup")]
+public class CoreSetupTests : IClassFixture<TestWebApplicationFactory>
+{
+    private readonly TestWebApplicationFactory _factory;
+
+    public CoreSetupTests(TestWebApplicationFactory factory)
+    {
+        _factory = factory;
+    }
+
+    [Fact]
+    public async Task GET_WithoutToken_Returns401()
+    {
+        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
+        {
+            AllowAutoRedirect = false
+        });
+
+        var response = await client.GetAsync("/api/agents");
+
+        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
+    }
+
+    [Fact]
+    public async Task GET_WithValidTestToken_Returns200()
+    {
+        var client = _factory.WithWebHostBuilder(builder =>
+        {
+            builder.ConfigureTestServices(services =>
+            {
+                services.AddAuthentication(TestAuthHandler.SchemeName)
+                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
+                        TestAuthHandler.SchemeName, _ => { });
+            });
+        }).CreateClient();
+
+        var response = await client.GetAsync("/api/agents");
+
+        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
+    }
+
+    [Fact]
+    public async Task Options_CorsPreflightFromLocalhost5173_ReturnsAllowedHeaders()
+    {
+        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
+        {
+            AllowAutoRedirect = false
+        });
+        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/agents");
+        request.Headers.Add("Origin", "http://localhost:5173");
+        request.Headers.Add("Access-Control-Request-Method", "GET");
+        request.Headers.Add("Access-Control-Request-Headers", "Authorization");
+
+        var response = await client.SendAsync(request);
+
+        Assert.True(
+            (int)response.StatusCode is >= 200 and < 300,
+            $"Expected 2xx CORS preflight response but got {(int)response.StatusCode}");
+        Assert.True(
+            response.Headers.Contains("Access-Control-Allow-Origin"),
+            "CORS response must include Access-Control-Allow-Origin header");
+    }
+
+    [Fact]
+    public async Task UseCors_BeforeUseAuthentication_PreflightDoesNotReturn401()
+    {
+        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
+        {
+            AllowAutoRedirect = false
+        });
+        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/agents");
+        request.Headers.Add("Origin", "http://localhost:5173");
+        request.Headers.Add("Access-Control-Request-Method", "GET");
+
+        var response = await client.SendAsync(request);
+
+        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
+    }
+
+    [Fact]
+    public async Task SignalRUpgrade_WithoutToken_IsRejected()
+    {
+        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
+        {
+            AllowAutoRedirect = false
+        });
+
+        // SignalR negotiate endpoint — requires [Authorize] on AgentTelemetryHub.
+        var response = await client.PostAsync("/hubs/agent/negotiate?negotiateVersion=1", null);
+
+        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
+    }
+
+    [Fact]
+    public async Task McpInvoke_Called11TimesRapidly_Returns429OnEleventh()
+    {
+        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
+        {
+            AllowAutoRedirect = false
+        });
+
+        HttpResponseMessage? lastResponse = null;
+        for (int i = 0; i < 11; i++)
+        {
+            lastResponse = await client.PostAsync("/api/mcp/tools/any/invoke", null);
+        }
+
+        // The GlobalLimiter in DependencyInjection.cs limits POST /api/mcp/tools/*
+        // to 10 req/min per IP. The 11th request must be rejected.
+        Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse!.StatusCode);
+    }
+}
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/TestAuthHandler.cs b/src/Content/Tests/Presentation.AgentHub.Tests/TestAuthHandler.cs
new file mode 100644
index 0000000..e96d60c
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/TestAuthHandler.cs
@@ -0,0 +1,33 @@
+using Microsoft.AspNetCore.Authentication;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+using System.Security.Claims;
+using System.Text.Encodings.Web;
+
+namespace Presentation.AgentHub.Tests;
+
+/// <summary>
+/// Stub authentication handler for integration tests.
+/// Authenticates all requests as a fixed test user without validating any token.
+/// Register as the default scheme in <c>ConfigureTestServices</c> to bypass Azure AD auth.
+/// </summary>
+/// <remarks>
+/// Replaced with a full implementation in section-07 that supports per-test claim customization.
+/// </remarks>
+public class TestAuthHandler(
+    IOptionsMonitor<AuthenticationSchemeOptions> options,
+    ILoggerFactory logger,
+    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
+{
+    /// <summary>Authentication scheme name used to override JWT bearer in integration tests.</summary>
+    public const string SchemeName = "TestAuth";
+
+    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
+    {
+        var claims = new[] { new Claim(ClaimTypes.Name, "test-user") };
+        var identity = new ClaimsIdentity(claims, SchemeName);
+        var principal = new ClaimsPrincipal(identity);
+        var ticket = new AuthenticationTicket(principal, SchemeName);
+        return Task.FromResult(AuthenticateResult.Success(ticket));
+    }
+}
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/TestJwtBearerHandler.cs b/src/Content/Tests/Presentation.AgentHub.Tests/TestJwtBearerHandler.cs
new file mode 100644
index 0000000..0661ae5
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/TestJwtBearerHandler.cs
@@ -0,0 +1,31 @@
+using Microsoft.AspNetCore.Authentication;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+using System.Text.Encodings.Web;
+
+namespace Presentation.AgentHub.Tests;
+
+/// <summary>
+/// No-op JWT Bearer handler used by <see cref="TestWebApplicationFactory"/> as the default
+/// authentication scheme. Mimics the real JWT Bearer handler behaviour for requests without
+/// a token: returns <see cref="AuthenticateResult.NoResult"/>, causing
+/// <c>UseAuthorization</c> to challenge with 401 for <c>[Authorize]</c> endpoints.
+/// The base-class <see cref="AuthenticationHandler{TOptions}.HandleChallengeAsync"/> sets
+/// <c>Response.StatusCode = 401</c>, so no override is needed.
+/// </summary>
+/// <remarks>
+/// This handler eliminates the need for valid Azure AD configuration in tests.
+/// Tests that require an authenticated user override the default scheme via
+/// <c>ConfigureTestServices</c> + <see cref="TestAuthHandler"/>.
+/// </remarks>
+public class TestJwtBearerHandler(
+    IOptionsMonitor<AuthenticationSchemeOptions> options,
+    ILoggerFactory logger,
+    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
+{
+    /// <summary>Authentication scheme name registered as the default in integration tests.</summary>
+    public const string SchemeName = "TestJwtBearer";
+
+    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
+        => Task.FromResult(AuthenticateResult.NoResult());
+}
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/TestWebApplicationFactory.cs b/src/Content/Tests/Presentation.AgentHub.Tests/TestWebApplicationFactory.cs
new file mode 100644
index 0000000..748d743
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/TestWebApplicationFactory.cs
@@ -0,0 +1,61 @@
+using Microsoft.AspNetCore.Authentication;
+using Microsoft.AspNetCore.Hosting;
+using Microsoft.AspNetCore.Mvc.Testing;
+using Microsoft.AspNetCore.TestHost;
+using Microsoft.Extensions.DependencyInjection;
+using Microsoft.Extensions.Hosting;
+
+namespace Presentation.AgentHub.Tests;
+
+/// <summary>
+/// Integration test factory for <c>Presentation.AgentHub</c>.
+/// Sets the working directory so <c>AppConfigHelper.LoadAppConfig()</c> can locate
+/// <c>appsettings.json</c>, activates the Development environment, and replaces
+/// Microsoft.Identity.Web's JWT Bearer handler with <see cref="TestJwtBearerHandler"/>
+/// so tests run without valid Azure AD configuration.
+/// </summary>
+/// <remarks>
+/// Fleshed out in section-07 with full auth overrides and per-test configuration helpers.
+/// </remarks>
+public class TestWebApplicationFactory : WebApplicationFactory<Program>
+{
+    protected override void ConfigureWebHost(IWebHostBuilder builder)
+    {
+        // AppConfigHelper.LoadAppConfig() reads appsettings.json from Directory.GetCurrentDirectory().
+        // In test context CWD is the test runner directory; redirect to the AgentHub
+        // assembly output directory so appsettings.json and appsettings.Development.json are found.
+        Directory.SetCurrentDirectory(
+            Path.GetDirectoryName(typeof(Program).Assembly.Location)!);
+
+        // Development environment loads appsettings.Development.json, which includes
+        // http://localhost:5173 in AllowedOrigins — required by the CORS integration tests.
+        builder.UseEnvironment("Development");
+
+        builder.ConfigureTestServices(services =>
+        {
+            // Replace Microsoft.Identity.Web's JWT Bearer handler with a no-op stub.
+            // TestJwtBearerHandler returns NoResult() when no token is present, causing
+            // UseAuthorization to challenge with 401 for [Authorize] endpoints — matching
+            // real JWT behaviour without requiring valid AzureAd configuration.
+            // Tests that need an authenticated user override this via WithWebHostBuilder +
+            // ConfigureTestServices using TestAuthHandler.
+            services.AddAuthentication(TestJwtBearerHandler.SchemeName)
+                .AddScheme<AuthenticationSchemeOptions, TestJwtBearerHandler>(
+                    TestJwtBearerHandler.SchemeName, _ => { });
+        });
+    }
+
+    protected override IHost CreateHost(IHostBuilder builder)
+    {
+        // The shared GetServices() DI has a pre-existing scope violation
+        // (MemoizedPromptComposer singleton consuming IAgentExecutionContext scoped).
+        // ConsoleUI avoids this because BuildServiceProvider() doesn't validate scopes.
+        // Suppress validation here to match that behavior until the upstream DI is corrected.
+        builder.UseDefaultServiceProvider(options =>
+        {
+            options.ValidateScopes = false;
+            options.ValidateOnBuild = false;
+        });
+        return base.CreateHost(builder);
+    }
+}
