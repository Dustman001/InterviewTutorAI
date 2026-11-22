using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TutorAI.Api;
using TutorAI.Api.Assessment;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;

namespace TutorAI.Tests.Integration;

/// <summary>Background grading against the real database, with a fake grader so no Claude call is made.</summary>
[Trait("Category", "Integration")]
public sealed class BackgroundGradingTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    // Fast retries, so a three-attempt failure takes a few seconds.
    private static readonly (string, string)[] QuickRetries =
        [("Assessment:RetryDelaySeconds:0", "0"), ("Assessment:RetryDelaySeconds:1", "0")];

    [E2EFact]
    public async Task End_answers_202_at_once_and_the_worker_grades_the_session()
    {
        var grader = new FakeGrader { Delay = TimeSpan.FromSeconds(2) };
        await using var api = CreateApi(grader);
        var client = await SignInAsync(api);
        var sessionId = await CreateSpokenSessionAsync(api, client);

        var timer = Stopwatch.StartNew();
        var end = await client.PostAsync($"/api/sessions/{sessionId}/end", null);
        timer.Stop();

        Assert.Equal(HttpStatusCode.Accepted, end.StatusCode);
        Assert.Equal("Pending", (await end.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"/end took {timer.Elapsed}");
        Assert.Contains(await GradingStateAsync(client, sessionId), new[] { "Pending", "Running" });

        var assessment = await WaitForAssessmentAsync(client, sessionId);

        Assert.True(assessment.Sufficient);
        Assert.Equal("B1", assessment.Levels.Grammar);
        Assert.Equal(FakeGrader.ModelName, assessment.Model);
        Assert.Equal(1, grader.Calls);
        using var db = database.Open();
        Assert.Equal(
            ("Done", 1),
            db.QuerySingle<(string, int)>("SELECT GradingState, GradingAttempts FROM dbo.Sessions WHERE Id = @sessionId", new { sessionId }));

        Assert.Equal(
            (1200, 800),
            db.QuerySingle<(int, int)>("SELECT InputTokens, OutputTokens FROM dbo.Assessments WHERE SessionId = @sessionId", new { sessionId }));
        var admin = (await client.GetFromJsonAsync<List<JsonElement>>("/api/users"))!
            .Single(u => u.GetProperty("username").GetString() == TestApi.AdminUsername);
        var usage = admin.GetProperty("usage30Days");
        Assert.True(usage.GetProperty("tokens").GetInt64() >= 2000);
        // The fake model has no configured price: counted, not costed.
        Assert.Contains(FakeGrader.ModelName, usage.GetProperty("unpricedModels").EnumerateArray().Select(m => m.GetString()));

        // Ending again returns the stored assessment without grading again.
        var again = await client.PostAsync($"/api/sessions/{sessionId}/end", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, grader.Calls);
    }

    [E2EFact]
    public async Task A_failing_grader_ends_in_Failed_after_three_attempts_and_try_again_succeeds()
    {
        var grader = new FakeGrader { FailFirst = 3 };
        await using var api = CreateApi(grader, QuickRetries);
        var client = await SignInAsync(api);
        var sessionId = await CreateSpokenSessionAsync(api, client);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/sessions/{sessionId}/end", null)).StatusCode);
        var failed = await WaitForGradingAsync(client, sessionId, state => state == "Failed");

        Assert.Equal(3, grader.Calls);
        Assert.Contains("3 attempts", failed.GetProperty("grading").GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("assessment").ValueKind);
        using (var db = database.Open())
        {
            Assert.Equal(3, db.ExecuteScalar<int>("SELECT GradingAttempts FROM dbo.Sessions WHERE Id = @sessionId", new { sessionId }));
        }

        // "Try again" on the summary page is a plain /end.
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/sessions/{sessionId}/end", null)).StatusCode);
        var assessment = await WaitForAssessmentAsync(client, sessionId);

        Assert.True(assessment.Sufficient);
        Assert.Equal(4, grader.Calls);
    }

    [E2EFact]
    public async Task Orphaned_running_jobs_are_reclaimed_at_startup_or_failed_on_their_last_attempt()
    {
        Guid retried, exhausted;
        await using (var first = CreateApi(new FakeGrader()))
        {
            var client = await SignInAsync(first);
            retried = await CreateSpokenSessionAsync(first, client);
            exhausted = await CreateSpokenSessionAsync(first, client);
        }

        // As if an instance died mid-grading 11 minutes ago (the stale limit is 10).
        using (var db = database.Open())
        {
            db.Execute(
                """
                UPDATE dbo.Sessions
                SET Status = 'Ended', EndedAt = SYSUTCDATETIME(), GradingState = 'Running',
                    GradingStartedAt = DATEADD(minute, -11, SYSUTCDATETIME()),
                    GradingAttempts = CASE WHEN Id = @retried THEN 1 ELSE 3 END
                WHERE Id IN (@retried, @exhausted)
                """,
                new { retried, exhausted });
        }

        var grader = new FakeGrader();
        await using var second = CreateApi(grader);
        _ = second.Services; // starts the host, and with it the worker's startup sweep

        await WaitUntilAsync(() => State(retried) == ("Done", 2) && State(exhausted).State == "Failed");
        Assert.Equal(1, grader.Calls);
        using var check = database.Open();
        Assert.Contains("stopped unexpectedly", check.ExecuteScalar<string>(
            "SELECT GradingError FROM dbo.Sessions WHERE Id = @exhausted", new { exhausted }));
    }

    [E2EFact]
    public async Task Shutting_down_mid_grading_puts_the_job_back_without_using_an_attempt()
    {
        var grader = new FakeGrader { Delay = TimeSpan.FromMinutes(1) };
        Guid sessionId;
        await using (var api = CreateApi(grader))
        {
            var client = await SignInAsync(api);
            sessionId = await CreateSpokenSessionAsync(api, client);
            await client.PostAsync($"/api/sessions/{sessionId}/end", null);
            await WaitUntilAsync(() => State(sessionId).State == "Running");
        }

        Assert.Equal(("Pending", 0), State(sessionId));

        // Leave nothing queued: the next test's worker would pick it up at startup.
        using var db = database.Open();
        db.Execute("DELETE FROM dbo.Sessions WHERE Id = @sessionId", new { sessionId });
    }

    [E2EFact]
    public async Task Shutting_down_while_a_claim_is_in_flight_still_puts_the_job_back()
    {
        // The claim commits in the database, then shutdown begins before the worker has read the claimed row:
        // the window in which a cancellable claim threw and left the session Running with an attempt used.
        var claims = new HeldClaims();
        Guid sessionId;
        await using (var api = TestApi.CreateWithServices(
            database.ConnectionString,
            services =>
            {
                services.AddSingleton<ICefrGrader>(new FakeGrader());
                services.AddSingleton(sp => claims.Wrap(
                    ActivatorUtilities.CreateInstance<SessionRepository>(sp),
                    sp.GetRequiredService<IHostApplicationLifetime>()));
            }))
        {
            var client = await SignInAsync(api);
            sessionId = await CreateSpokenSessionAsync(api, client);
            await client.PostAsync($"/api/sessions/{sessionId}/end", null);
            await claims.Claimed.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(("Running", 1), State(sessionId));
        }

        Assert.Equal(("Pending", 0), State(sessionId));

        using var db = database.Open();
        db.Execute("DELETE FROM dbo.Sessions WHERE Id = @sessionId", new { sessionId });
    }

    [E2EFact]
    public async Task Concurrent_claims_hand_a_job_to_exactly_one_worker()
    {
        var sessionId = Guid.NewGuid();
        using (var db = database.Open())
        {
            db.Execute(
                """
                INSERT INTO dbo.Sessions (Id, Mode, ScenarioJson, Status, EndedAt, GradingState)
                VALUES (@sessionId, 'Conversation', '{}', 'Ended', SYSUTCDATETIME(), 'Pending')
                """,
                new { sessionId });
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TutorAI"] = database.ConnectionString })
            .Build();
        var workers = Enumerable.Range(0, 8).Select(_ => new SessionRepository(new SqlDatabase(configuration))).ToList();

        var claims = await Task.WhenAll(workers.Select(w => w.ClaimGradingAsync(10, 3, CancellationToken.None)));

        var won = Assert.Single(claims, c => c is not null);
        Assert.Equal(new GradingClaim(sessionId, 1), won);
        Assert.Equal(("Running", 1), State(sessionId));
    }

    [E2EFact]
    public void The_api_refuses_to_start_when_a_grading_could_outlive_its_claim()
    {
        // 600 s is exactly the 10-minute stale limit: the attempt could still be running when it is claimed again.
        using var api = CreateApi(new FakeGrader(), ("Assessment:GradingTimeoutSeconds", "600"), ("Assessment:StaleGradingMinutes", "10"));

        var error = Assert.ThrowsAny<Exception>(() => api.CreateClient());

        Assert.Contains("Assessment:GradingTimeoutSeconds must be less than", error.ToString());
    }

    [E2EFact]
    public void The_api_refuses_to_start_with_a_grading_timeout_that_is_not_positive()
    {
        foreach (var timeout in new[] { "0", "-1" })
        {
            using var api = CreateApi(new FakeGrader(), ("Assessment:GradingTimeoutSeconds", timeout));

            var error = Assert.ThrowsAny<Exception>(() => api.CreateClient());

            Assert.Contains("Assessment:GradingTimeoutSeconds must be greater than 0.", error.ToString());
        }
    }

    private WebApplicationFactory<Program> CreateApi(FakeGrader grader, params (string, string)[] settings) =>
        TestApi.CreateWithServices(
            database.ConnectionString, services => services.AddSingleton<ICefrGrader>(grader), settings);

    private static Task<HttpClient> SignInAsync(WebApplicationFactory<Program> api) =>
        TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);

    /// <summary>A session with enough learner words to be graded, seeded directly so no Claude call is made.</summary>
    private static async Task<Guid> CreateSpokenSessionAsync(WebApplicationFactory<Program> api, HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sessions", new { mode = "Conversation", scenario = new { topic = "Travel", durationMinutes = 10 } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sessionId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var text = string.Join(' ', Enumerable.Repeat("I went to the market and bought some fresh fruit.", 18));
        await api.Services.GetRequiredService<ISessionRepository>().AddTurnAsync(
            new TurnRow { SessionId = sessionId, Role = TurnRole.User, Text = text, WordCount = text.Split(' ').Length },
            CancellationToken.None);
        return sessionId;
    }

    private static async Task<string?> GradingStateAsync(HttpClient client, Guid sessionId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}")).GetProperty("grading").GetProperty("state").GetString();

    private static async Task<JsonElement> WaitForGradingAsync(HttpClient client, Guid sessionId, Func<string?, bool> until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var detail = await client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}");
            if (until(detail.GetProperty("grading").GetProperty("state").GetString()))
            {
                return detail;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Grading did not finish: {detail.GetProperty("grading")}");
            await Task.Delay(250);
        }
    }

    private static async Task<AssessmentResponse> WaitForAssessmentAsync(HttpClient client, Guid sessionId)
    {
        var detail = await WaitForGradingAsync(client, sessionId, state => state is "Done" or "Failed");
        Assert.Equal("Done", detail.GetProperty("grading").GetProperty("state").GetString());
        return detail.GetProperty("assessment").Deserialize<AssessmentResponse>(Json)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the grading state.");
            await Task.Delay(250);
        }
    }

    private (string? State, int Attempts) State(Guid sessionId)
    {
        using var db = database.Open();
        return db.QuerySingle<(string?, int)>(
            "SELECT GradingState, GradingAttempts FROM dbo.Sessions WHERE Id = @sessionId", new { sessionId });
    }
}

/// <summary>
/// Holds the first successful grading claim until the host starts stopping. If the claim was made with a
/// cancellable token, it then fails once that token is cancelled, as Dapper does when cancelled after the
/// database has committed but before the row is read.
/// </summary>
internal sealed class HeldClaims
{
    private readonly TaskCompletionSource _claimed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _held;

    public Task Claimed => _claimed.Task;

    public ISessionRepository Wrap(ISessionRepository inner, IHostApplicationLifetime lifetime)
    {
        var proxy = DispatchProxy.Create<ISessionRepository, Proxy>();
        ((Proxy)(object)proxy).Init(inner, this, lifetime);
        return proxy;
    }

    private async Task<GradingClaim?> HoldAsync(Task<GradingClaim?> claiming, CancellationToken ct, CancellationToken stopping)
    {
        var claim = await claiming;
        if (claim is null || Interlocked.Exchange(ref _held, 1) == 1)
        {
            return claim;
        }

        _claimed.TrySetResult();
        await Task.Delay(Timeout.Infinite, stopping).ContinueWith(_ => { }, TaskScheduler.Default);
        if (ct.CanBeCanceled)
        {
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            ct.ThrowIfCancellationRequested();
        }

        return claim;
    }

    public class Proxy : DispatchProxy
    {
        private ISessionRepository _inner = null!;
        private HeldClaims _owner = null!;
        private IHostApplicationLifetime _lifetime = null!;

        internal void Init(ISessionRepository inner, HeldClaims owner, IHostApplicationLifetime lifetime) =>
            (_inner, _owner, _lifetime) = (inner, owner, lifetime);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                var result = targetMethod!.Invoke(_inner, args);
                return targetMethod.Name == nameof(ISessionRepository.ClaimGradingAsync)
                    ? _owner.HoldAsync((Task<GradingClaim?>)result!, (CancellationToken)args![2]!, _lifetime.ApplicationStopping)
                    : result;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }
        }
    }
}

/// <summary>Stands in for Claude: fast, free, and can be told to fail or to take its time.</summary>
internal sealed class FakeGrader : ICefrGrader
{
    public const string ModelName = "fake-grader";

    private static readonly DimensionGrade Grade = new("B1", "medium", ["I went to the market"], "Use more varied past tenses.");

    private int _calls;

    /// <summary>The first N calls throw, as a failing Claude call would.</summary>
    public int FailFirst { get; init; }

    public TimeSpan Delay { get; init; }

    public List<Correction> Corrections { get; init; } = [];

    public int Calls => Volatile.Read(ref _calls);

    public string Model => ModelName;

    public string PromptVersion => "fake-1";

    public async Task<GradingResult> GradeAsync(
        SessionRow session, IReadOnlyList<TurnRow> turns, FluencyStats fluency, ClaudeUsage usage, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        usage.Model = ModelName;
        usage.InputTokens = 1200;
        usage.OutputTokens = 800;
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        if (call <= FailFirst)
        {
            throw new HttpRequestException($"Fake grading failure {call}.");
        }

        return new GradingResult(Grade, Grade, Grade, Grade, "You did well.", Corrections);
    }
}
