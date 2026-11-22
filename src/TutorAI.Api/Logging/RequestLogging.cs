using System.Diagnostics;
using TutorAI.Api.Auth;

namespace TutorAI.Api.Logging;

/// <summary>
/// Logs every response with status 400 or above, once, with the route template, status, user and trace id.
/// 401/403/413/429 are Information (access and abuse signals), 5xx returned without an exception (e.g. a 502 from
/// an upstream) are Warning, other 4xx are Debug. Unhandled exceptions are logged by the exception handler, so an
/// exception from the rest of the pipeline passes through here without a log entry.
/// </summary>
/// <remarks>
/// Puts neither the request path nor the query in its message or properties: they can hold user-supplied text. The
/// route template comes from the matched endpoint. (ASP.NET Core's own request scope does carry the raw path as
/// RequestPath, and it is exported with the other scopes when Application Insights is on — as in request telemetry.)
/// </remarks>
public sealed class RequestOutcomeLogging(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, ILogger<RequestOutcomeLogging> logger)
    {
        var start = Stopwatch.GetTimestamp();
        await next(http);

        var status = http.Response.StatusCode;
        if (status < StatusCodes.Status400BadRequest)
        {
            return;
        }

        var level = LevelFor(status);
        if (!logger.IsEnabled(level))
        {
            return;
        }

        logger.Log(
            level,
            "{Method} {Route} responded {StatusCode} for user {UserId} in {ElapsedMs} ms (trace {TraceId})",
            http.Request.Method,
            (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(none)",
            status,
            http.User.FindFirst(TokenService.UserIdClaim)?.Value,
            (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier);
    }

    public static LogLevel LevelFor(int status) => status switch
    {
        >= StatusCodes.Status500InternalServerError => LogLevel.Warning,
        StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden or StatusCodes.Status413PayloadTooLarge
            or StatusCodes.Status429TooManyRequests => LogLevel.Information,
        _ => LogLevel.Debug,
    };
}

/// <summary>Adds the signed-in user's id to every log entry written while the request runs.</summary>
public sealed class UserLogScope(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, ILogger<UserLogScope> logger)
    {
        if (http.User.FindFirst(TokenService.UserIdClaim)?.Value is not { } userId)
        {
            await next(http);
            return;
        }

        using (logger.BeginScope(new Dictionary<string, object?> { ["UserId"] = userId }))
        {
            await next(http);
        }
    }
}

public static class RequestLoggingExtensions
{
    /// <summary>Place inside the exception handler, so exceptions are logged by the handler only.</summary>
    public static IApplicationBuilder UseRequestOutcomeLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestOutcomeLogging>();

    /// <summary>Place after authentication, which sets the user.</summary>
    public static IApplicationBuilder UseUserLogScope(this IApplicationBuilder app) => app.UseMiddleware<UserLogScope>();
}
