namespace Domain.Common.Constants;

/// <summary>
/// Constant definitions for policy names used throughout the application
/// for HTTP resilience, CORS, and rate limiting configuration.
/// </summary>
/// <remarks>
/// Using constants ensures consistency when referencing policies across
/// the application — in attribute parameters, middleware registration,
/// and endpoint configuration.
/// <para><b>Policy Categories:</b></para>
/// <list type="bullet">
///   <item>HTTP Polly Policies - Resilience patterns for external HTTP calls</item>
///   <item>CORS Policies - Cross-Origin Resource Sharing configuration</item>
///   <item>Rate Limiter Policies - Request throttling and rate limiting</item>
/// </list>
/// </remarks>
public static class PolicyNameConstants
{
    // HTTP Polly Policies

    /// <summary>
    /// Policy name for the HTTP circuit breaker pattern.
    /// Stops calling failing services after a threshold of failures,
    /// allowing the downstream service to recover.
    /// </summary>
    public const string HTTP_CIRCUIT_BREAKER = "HttpCircuitBreaker";

    /// <summary>
    /// Policy name for the HTTP retry pattern.
    /// Automatically retries failed HTTP requests with exponential backoff.
    /// </summary>
    public const string HTTP_RETRY = "HttpRetry";

    /// <summary>
    /// Policy name for the HTTP timeout policy.
    /// Prevents requests from hanging indefinitely.
    /// </summary>
    public const string HTTP_TIMEOUT = "HttpTimeout";

    // HTTP CORS Policies

    /// <summary>
    /// Policy name for general CORS configuration with allowed origins from config.
    /// </summary>
    public const string CORS_CONFIG_POLICY = "CorsConfigPolicy";

    /// <summary>
    /// Policy name for MCP Server CORS policy with restricted methods and preflight caching.
    /// </summary>
    public const string CORS_AI_MCPSERVER_POLICY = "CorsAIMCPServerPolicy";

    /// <summary>
    /// Policy name for AI Copilot CORS policy with method and header restrictions.
    /// </summary>
    public const string CORS_AI_COPILOT_POLICY = "CorsAICopilotPolicy";

    // HTTP Rate Limiter Policies

    /// <summary>
    /// Policy name for the default AI rate limiter (100 requests/minute, fixed window).
    /// </summary>
    public const string RATE_LIMITER_AI_DEFAULT_POLICY = "RateLimiterAIDefaultPolicy";

    /// <summary>
    /// Policy name for the MCP Server rate limiter (100 requests/minute, fixed window).
    /// </summary>
    public const string RATE_LIMITER_AI_MCPSERVER_POLICY = "RateLimiterAIMCPServerPolicy";
}
