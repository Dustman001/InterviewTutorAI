using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TutorAI.Api;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;
using TutorAI.Api.Prep;
using TutorAI.Api.Speech;
using TutorAI.Tests.Logging;

namespace TutorAI.Tests.Integration;

/// <summary>What the API logs for failed requests, unhandled exceptions and sign-in security events.</summary>
[Trait("Category", "Integration")]
public sealed class ErrorLoggingTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    private const string Outcome = "TutorAI.Api.Logging.RequestOutcomeLogging";
    private const string Auth = "TutorAI.Api.Endpoints.AuthEndpoints";
    private const string ExceptionHandler = "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware";
    private const string Password = "Right-Pass-123";

    private readonly CapturingLoggerProvider _logs = new();

    [E2EFact]
    public async Task Unhandled_exception_is_logged_once_and_returns_problem_details_with_trace_id()
    {
        await using var api = Api(services => services.AddSingleton(FailingPrepRepository.Create(hang: false)));
        var guest = await TestApi.SignInGuestAsync(api);

        var response = await guest.GetAsync("/api/prep/runs");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var traceId = JsonDocument.Parse(body).RootElement.GetProperty("traceId").GetString();
        Assert.DoesNotContain(FailingPrepRepository.Message, body);

        await WaitForAsync(() => _logs.From(ExceptionHandler).Any(e => e.Level == LogLevel.Error));
        var errors = _logs.Entries.Where(e => e.Exception?.Message == FailingPrepRepository.Message).ToArray();
        var error = Assert.Single(errors);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal(ExceptionHandler, error.Category);
        // The body's traceId is what an operator searches for: the bare trace id (Application Insights operation_Id),
        // the same value the log entry carries, not the full W3C traceparent.
        Assert.Matches("^[0-9a-f]{32}$", traceId);
        Assert.Equal(error.Scopes["TraceId"]?.ToString(), traceId);
        Assert.DoesNotContain(_logs.From(Outcome), e => Equals(e["Route"], "/api/prep/runs"));
    }

    [E2EFact]
    public async Task Not_found_is_logged_at_debug_with_route_and_user()
    {
        await using var api = Api();
        var guest = await TestApi.SignInGuestAsync(api);
        var guestId = await UserIdAsync(api, TestApi.GuestUsername);

        var response = await guest.GetAsync($"/api/sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var entry = await OutcomeAsync(404);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("GET", entry["Method"]);
        Assert.Equal("/api/sessions/{id:guid}", entry["Route"]);
        Assert.Equal(guestId.ToString(), entry["UserId"]);
        Assert.False(string.IsNullOrEmpty(entry["TraceId"] as string));
    }

    [E2EFact]
    public async Task Anonymous_request_is_logged_as_401_information()
    {
        await using var api = Api();

        var response = await api.CreateClient().GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var entry = await OutcomeAsync(401);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("/api/auth/me", entry["Route"]);
        Assert.Null(entry["UserId"]);
    }

    [E2EFact]
    public async Task Oversized_request_is_logged_as_413()
    {
        await using var api = Api(settings: ("Sessions:MaxRequestBytes", "50000"));
        var body = new StringContent(
            JsonSerializer.Serialize(new { text = "Hi", padding = new string('x', 60_000) }), Encoding.UTF8, "application/json");

        var response = await api.CreateClient().PostAsync($"/api/sessions/{Guid.NewGuid()}/turns", body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var entry = await OutcomeAsync(413);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("POST", entry["Method"]);
    }

    [E2EFact]
    public async Task Session_logs_carry_user_and_session_scope()
    {
        await using var api = Api(services => services.AddSingleton<ITutorChat, FailingTutorChat>());
        var guest = await TestApi.SignInGuestAsync(api);
        var guestId = await UserIdAsync(api, TestApi.GuestUsername);
        var created = await guest.PostAsJsonAsync(
            "/api/sessions", new { mode = "Conversation", scenario = new { topic = "Travel", durationMinutes = 10 } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var sessionId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var turn = await guest.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", new { text = "Hello, I would like to book a room." });
        await turn.Content.ReadAsStringAsync();

        await WaitForAsync(() => _logs.Entries.Any(e => e.Exception?.Message == FailingTutorChat.Message));
        var entry = _logs.Entries.Single(e => e.Exception?.Message == FailingTutorChat.Message);
        Assert.Equal("TutorAI.Api.Conversation.TurnService", entry.Category);
        Assert.Equal(guestId.ToString(), entry.Scopes["UserId"]);
        Assert.Equal(sessionId.ToString(), entry.Scopes["SessionId"]?.ToString());
    }

    [E2EFact]
    public async Task Speech_token_rejection_logs_a_warning_with_the_upstream_status()
    {
        const string key = "fake-speech-key-123";
        await using var api = Api(
            services => services.AddHttpClient<SpeechTokenService>()
                .ConfigurePrimaryHttpMessageHandler(() => new StatusHandler(HttpStatusCode.Unauthorized)),
            ("AzureSpeech:Key", key), ("AzureSpeech:Region", "westeurope"));
        var guest = await TestApi.SignInGuestAsync(api);

        var response = await guest.GetAsync("/api/speech/token");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var warning = await WaitForEntryAsync(
            e => e.Category == "TutorAI.Api.Speech.SpeechTokenService" && e.Level == LogLevel.Warning);
        Assert.Equal(401, warning["StatusCode"]);
        Assert.IsType<HttpRequestException>(warning.Exception);
        Assert.Equal(LogLevel.Warning, (await OutcomeAsync(502)).Level);
        Assert.DoesNotContain(_logs.Entries, e => e.AllText().Contains(key));
    }

    [E2EFact]
    public async Task Wrong_password_on_a_known_account_logs_the_user_id_only()
    {
        await using var api = Api();
        var (username, id) = await CreateUserAsync(api);

        var response = await LoginAsync(api, username, "wrong-password-xyz");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var entry = await WaitForEntryAsync(e => e.Category == Auth && e.Level == LogLevel.Information);
        Assert.Equal(id, entry["UserId"]);
        Assert.Contains("wrong password", entry.Message);
        await OutcomeAsync(401);
        Assert.DoesNotContain(_logs.Entries, e => e.AllText().Contains(username) || e.AllText().Contains("wrong-password-xyz"));
    }

    [E2EFact]
    public async Task Wrong_current_password_on_change_logs_the_user_id_only()
    {
        await using var api = Api();
        var (username, id) = await CreateUserAsync(api);
        var client = await TestApi.SignInAsync(api, username, Password);

        var response = await client.PostAsJsonAsync(
            "/api/auth/password", new { currentPassword = "wrong-current-xyz", newPassword = "New-Pass-456" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = await WaitForEntryAsync(e => e.Category == Auth && e.Level == LogLevel.Information);
        Assert.Equal(id, entry["UserId"]);
        Assert.DoesNotContain(_logs.Entries, e => e.AllText().Contains("wrong-current-xyz") || e.AllText().Contains("New-Pass-456"));
    }

    [E2EFact]
    public async Task Fifth_wrong_password_logs_the_lockout_and_locked_refusal_is_logged()
    {
        await using var api = Api();
        var (username, id) = await CreateUserAsync(api);
        SetFailures(username, 4);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(api, username, "wrong-password")).StatusCode);
        var locked = await WaitForEntryAsync(e => e.Category == Auth && e.Level == LogLevel.Warning);
        Assert.Equal(id, locked["UserId"]);
        Assert.IsType<DateTime>(locked["LockedUntil"]);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(api, username, Password)).StatusCode);
        var refused = await WaitForEntryAsync(e => e.Category == Auth && e.Message.Contains("refused for locked account"));
        Assert.Equal(LogLevel.Information, refused.Level);
        Assert.Equal(id, refused["UserId"]);
    }

    [E2EFact]
    public async Task Unknown_username_is_not_logged_by_auth()
    {
        await using var api = Api();
        var username = "nobody-" + Guid.NewGuid().ToString("N")[..10];

        var response = await LoginAsync(api, username, "whatever-pass");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null((await OutcomeAsync(401))["UserId"]);
        Assert.Empty(_logs.From(Auth));
        Assert.DoesNotContain(_logs.Entries, e => e.AllText().Contains(username));
    }

    [E2EFact]
    public async Task Disabled_account_sign_in_is_logged_as_warning()
    {
        await using var api = Api();
        var (username, id) = await CreateUserAsync(api);
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/users/{id}", new { isDisabled = true })).StatusCode);

        var response = await LoginAsync(api, username, Password);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var entry = await WaitForEntryAsync(e => e.Category == Auth && e.Level == LogLevel.Warning);
        Assert.Equal(id, entry["UserId"]);
        Assert.Contains("disabled", entry.Message);
    }

    [E2EFact]
    public async Task Rate_limited_requests_are_counted_by_policy()
    {
        await using var api = Api(settings: ("Auth:LoginAttemptsPerMinute", "1"));
        long counted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Telemetry.Name && instrument.Name == "tutorai.requests.rate_limited")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag is { Key: "policy", Value: "login" })
                {
                    Interlocked.Add(ref counted, value);
                }
            }
        });
        listener.Start();

        await LoginAsync(api, "nobody-a", "whatever-pass");
        var second = await LoginAsync(api, "nobody-b", "whatever-pass");

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.True(Interlocked.Read(ref counted) >= 1, "expected the login policy rejection to be counted");
        Assert.Equal(LogLevel.Information, (await OutcomeAsync(429)).Level);
    }

    [E2EFact]
    public async Task Client_abort_is_not_logged_as_error()
    {
        var repository = FailingPrepRepository.Create(hang: true);
        await using var api = Api(services => services.AddSingleton(repository));
        var guest = await TestApi.SignInGuestAsync(api);

        using var abort = new CancellationTokenSource();
        var request = guest.GetAsync("/api/prep/runs", abort.Token);
        await FailingPrepRepository.Entered(repository).WaitAsync(TimeSpan.FromSeconds(10));
        await abort.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        // The server side finishes after the client has gone; wait for the handler or the outcome line.
        await WaitForAsync(() => _logs.From(ExceptionHandler).Count > 0 || _logs.From(Outcome).Any(e => Equals(e["Route"], "/api/prep/runs")));
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Error);
    }

    private WebApplicationFactory<Program> Api(
        Action<IServiceCollection>? services = null, params (string Key, string Value)[] settings) =>
        TestApi.CreateWithServices(
            database.ConnectionString,
            s =>
            {
                s.AddCapturingLogger(_logs);
                services?.Invoke(s);
            },
            settings);

    private Task<LogEntry> OutcomeAsync(int status) =>
        WaitForEntryAsync(e => e.Category == Outcome && Equals(e["StatusCode"], status));

    /// <summary>
    /// The outcome line is written after the response has gone back to the client, so it can trail the call.
    /// </summary>
    private async Task<LogEntry> WaitForEntryAsync(Func<LogEntry, bool> match)
    {
        await WaitForAsync(() => _logs.Entries.Any(match));
        return _logs.Entries.First(match);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("The expected log entry was not written within 10 s.");
            }

            await Task.Delay(20);
        }
    }

    private static Task<HttpResponseMessage> LoginAsync(WebApplicationFactory<Program> api, string username, string password) =>
        api.CreateClient().PostAsJsonAsync("/api/auth/login", new { username, password });

    /// <summary>A fresh guest per test, so lock state can't leak between tests.</summary>
    private static async Task<(string Username, int Id)> CreateUserAsync(WebApplicationFactory<Program> api)
    {
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var username = "log-" + Guid.NewGuid().ToString("N")[..10];
        var response = await admin.PostAsJsonAsync("/api/users", new { username, password = Password, role = "Guest", sessionLimit = 3 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (username, await UserIdAsync(api, username));
    }

    private static async Task<int> UserIdAsync(WebApplicationFactory<Program> api, string username)
    {
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        return (await admin.GetFromJsonAsync<List<JsonElement>>("/api/users"))!
            .Single(u => u.GetProperty("username").GetString() == username).GetProperty("id").GetInt32();
    }

    private void SetFailures(string username, int failures)
    {
        using var db = database.Open();
        db.Execute(
            "UPDATE dbo.Users SET FailedLoginCount = @failures, LockedUntil = NULL WHERE Username = @username",
            new { username, failures });
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class FailingTutorChat : ITutorChat
    {
        public const string Message = "tutor chat failed (test)";

        public IAsyncEnumerable<string> StreamReplyAsync(
            SessionRow session, IReadOnlyList<TurnRow> turns, ClaudeUsage? usage = null, CancellationToken ct = default) =>
            throw new InvalidOperationException(Message);
    }

    /// <summary>
    /// An <see cref="IPrepRepository"/> whose every call throws, or with <c>hang</c> waits until its cancellation
    /// token fires (to simulate a client abort mid-request).
    /// </summary>
    public class FailingPrepRepository : DispatchProxy
    {
        public const string Message = "prep repository failed (test)";

        private bool _hang;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static IPrepRepository Create(bool hang)
        {
            var proxy = DispatchProxy.Create<IPrepRepository, FailingPrepRepository>();
            ((FailingPrepRepository)(object)proxy)._hang = hang;
            return proxy;
        }

        public static Task Entered(IPrepRepository proxy) => ((FailingPrepRepository)(object)proxy)._entered.Task;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _entered.TrySetResult();
            if (!_hang)
            {
                throw new InvalidOperationException(Message);
            }

            var ct = args?.OfType<CancellationToken>().FirstOrDefault() ?? CancellationToken.None;
            var returnType = targetMethod!.ReturnType;
            return returnType.IsGenericType
                ? typeof(FailingPrepRepository).GetMethod(nameof(HangAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, [ct])
                : Task.Delay(Timeout.Infinite, ct);
        }

        private static async Task<T> HangAsync<T>(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        }
    }
}
