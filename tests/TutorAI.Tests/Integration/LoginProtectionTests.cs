using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TutorAI.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class LoginProtectionTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    private const string Password = "Right-Pass-123";

    [E2EFact]
    public async Task Five_wrong_passwords_lock_the_account_even_against_the_right_one()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var username = await CreateUserAsync(api);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(api, username, "wrong-password")).StatusCode);
        }

        var fifth = await TimedLoginAsync(api, username, "wrong-password");
        var locked = await LoginAsync(api, username, Password);

        Assert.Equal(HttpStatusCode.Unauthorized, fifth.Response.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Contains("Try again", await locked.Content.ReadAsStringAsync());
        Assert.InRange(int.Parse(locked.Headers.GetValues("Retry-After").Single()), 1, 60);
        var (count, lockedUntil) = LockState(username);
        Assert.Equal(5, count);
        AssertLockedFor(TimeSpan.FromSeconds(60), lockedUntil, fifth);
    }

    [E2EFact]
    public async Task Each_failure_after_the_lock_doubles_it_up_to_the_maximum()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var username = await CreateUserAsync(api);
        SetLockState(username, failures: 5, lockedUntilSecondsFromNow: -1);

        var sixth = await TimedLoginAsync(api, username, "wrong-password");
        AssertLockedFor(TimeSpan.FromSeconds(120), LockState(username).LockedUntil, sixth);

        SetLockState(username, failures: 50, lockedUntilSecondsFromNow: -1);
        var fiftyFirst = await TimedLoginAsync(api, username, "wrong-password");
        AssertLockedFor(TimeSpan.FromSeconds(900), LockState(username).LockedUntil, fiftyFirst);
    }

    [E2EFact]
    public async Task An_expired_lock_lets_the_right_password_in_and_success_resets_the_count()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var username = await CreateUserAsync(api);
        SetLockState(username, failures: 7, lockedUntilSecondsFromNow: -1);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(api, username, Password)).StatusCode);

        Assert.Equal((0, (DateTime?)null), LockState(username));
    }

    [E2EFact]
    public async Task Admin_password_reset_clears_the_lock()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var username = await CreateUserAsync(api);
        SetLockState(username, failures: 6, lockedUntilSecondsFromNow: 600);

        var id = (await admin.GetFromJsonAsync<List<System.Text.Json.JsonElement>>("/api/users"))!
            .Single(u => u.GetProperty("username").GetString() == username).GetProperty("id").GetInt32();
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await admin.PostAsJsonAsync($"/api/users/{id}/password", new { password = "New-Pass-456" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(api, username, "New-Pass-456")).StatusCode);
    }

    [E2EFact]
    public async Task Unknown_usernames_are_not_counted_or_revealed()
    {
        await using var api = TestApi.Create(database.ConnectionString);

        var response = await LoginAsync(api, "nobody-" + Guid.NewGuid().ToString("N")[..8], "whatever-password");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Wrong username or password", await response.Content.ReadAsStringAsync());
    }

    [E2EFact]
    public async Task Sign_in_attempts_across_all_users_are_rate_limited()
    {
        await using var api = TestApi.Create(database.ConnectionString, null, ("Auth:LoginAttemptsPerMinute", "3"));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await LoginAsync(api, $"spray-{i}", "guess-password")).StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests],
            statuses);
    }

    /// <summary>A sign-in with this machine's clock read just before and after, the clock the API locks with.</summary>
    private static async Task<(HttpResponseMessage Response, DateTime Before, DateTime After)> TimedLoginAsync(
        WebApplicationFactory<Program> api, string username, string password)
    {
        var before = DateTime.UtcNow;
        var response = await LoginAsync(api, username, password);
        return (response, before, DateTime.UtcNow);
    }

    private static void AssertLockedFor(
        TimeSpan expected, DateTime? lockedUntil, (HttpResponseMessage Response, DateTime Before, DateTime After) attempt)
    {
        Assert.NotNull(lockedUntil);
        // datetime parameters round to about 3 ms.
        var slack = TimeSpan.FromMilliseconds(10);
        Assert.InRange(lockedUntil.Value, attempt.Before + expected - slack, attempt.After + expected + slack);
    }

    private static Task<HttpResponseMessage> LoginAsync(WebApplicationFactory<Program> api, string username, string password) =>
        api.CreateClient().PostAsJsonAsync("/api/auth/login", new { username, password });

    /// <summary>A fresh guest per test, so a lock in one test can't leak into another.</summary>
    private static async Task<string> CreateUserAsync(WebApplicationFactory<Program> api)
    {
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var username = "lock-" + Guid.NewGuid().ToString("N")[..10];
        var response = await admin.PostAsJsonAsync("/api/users", new { username, password = Password, role = "Guest", sessionLimit = 3 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return username;
    }

    private (int Count, DateTime? LockedUntil) LockState(string username)
    {
        using var db = database.Open();
        var (count, lockedUntil) = db.QuerySingle<(int, DateTime?)>(
            "SELECT FailedLoginCount, LockedUntil FROM dbo.Users WHERE Username = @username", new { username });
        return (count, lockedUntil is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null);
    }

    private void SetLockState(string username, int failures, int lockedUntilSecondsFromNow)
    {
        using var db = database.Open();
        db.Execute(
            """
            UPDATE dbo.Users
            SET FailedLoginCount = @failures, LockedUntil = DATEADD(second, @lockedUntilSecondsFromNow, SYSUTCDATETIME())
            WHERE Username = @username
            """,
            new { username, failures, lockedUntilSecondsFromNow });
    }
}
