using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using TutorAI.Api.Auth;
using TutorAI.Api.Logging;

namespace TutorAI.Tests.Logging;

public sealed class RequestOutcomeLoggingTests
{
    private const string Category = "TutorAI.Api.Logging.RequestOutcomeLogging";

    private readonly CapturingLoggerProvider _provider = new();

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(302)]
    [InlineData(399)]
    public async Task Responses_below_400_are_not_logged(int status)
    {
        await RunAsync(NewContext(), status);

        Assert.Empty(_provider.Entries);
    }

    [Theory]
    [InlineData(400, LogLevel.Debug)]
    [InlineData(404, LogLevel.Debug)]
    [InlineData(405, LogLevel.Debug)]
    [InlineData(409, LogLevel.Debug)]
    [InlineData(401, LogLevel.Information)]
    [InlineData(403, LogLevel.Information)]
    [InlineData(413, LogLevel.Information)]
    [InlineData(429, LogLevel.Information)]
    [InlineData(500, LogLevel.Warning)]
    [InlineData(502, LogLevel.Warning)]
    [InlineData(503, LogLevel.Warning)]
    public async Task Level_follows_the_status(int status, LogLevel expected)
    {
        await RunAsync(NewContext(), status);

        var entry = Assert.Single(_provider.Entries);
        Assert.Equal(Category, entry.Category);
        Assert.Equal(expected, entry.Level);
        Assert.Equal(status, entry["StatusCode"]);
    }

    [Fact]
    public async Task Entry_has_route_template_status_user_and_trace()
    {
        var http = NewContext("GET", "/api/sessions/7d4b0c52-6a43-4b6a-9b9a-2b8f3d6f1e10");
        http.SetEndpoint(Endpoint("/api/sessions/{id:guid}"));
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(TokenService.UserIdClaim, "42")], "test"));

        await RunAsync(http, 404);

        var entry = Assert.Single(_provider.Entries);
        Assert.Equal("GET", entry["Method"]);
        Assert.Equal("/api/sessions/{id:guid}", entry["Route"]);
        Assert.Equal(404, entry["StatusCode"]);
        Assert.Equal("42", entry["UserId"]);
        Assert.False(string.IsNullOrEmpty(entry["TraceId"] as string));
        Assert.IsType<long>(entry["ElapsedMs"]);
    }

    [Fact]
    public async Task Unmatched_route_logs_none_and_anonymous_user_logs_null()
    {
        await RunAsync(NewContext("GET", "/nothing-here"), 404);

        var entry = Assert.Single(_provider.Entries);
        Assert.Equal("(none)", entry["Route"]);
        Assert.Null(entry["UserId"]);
    }

    [Fact]
    public async Task Exception_from_next_propagates_without_a_log_entry()
    {
        var middleware = new RequestOutcomeLogging(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(NewContext(), Logger()));

        Assert.Empty(_provider.Entries);
    }

    [Fact]
    public async Task Path_and_query_are_never_logged()
    {
        var http = NewContext("GET", "/api/x/secret-value");
        http.Request.QueryString = new QueryString("?token=abc123");

        await RunAsync(http, 400);

        var entry = Assert.Single(_provider.Entries);
        Assert.DoesNotContain("secret-value", entry.AllText());
        Assert.DoesNotContain("abc123", entry.AllText());
    }

    private async Task RunAsync(HttpContext http, int status)
    {
        var middleware = new RequestOutcomeLogging(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(http, Logger());
    }

    private ILogger<RequestOutcomeLogging> Logger() =>
        LoggerFactory.Create(builder => builder.AddProvider(_provider).SetMinimumLevel(LogLevel.Trace))
            .CreateLogger<RequestOutcomeLogging>();

    private static DefaultHttpContext NewContext(string method = "GET", string path = "/api/test")
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        return http;
    }

    private static RouteEndpoint Endpoint(string pattern) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, EndpointMetadataCollection.Empty, "test");
}
