using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TutorAI.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class PasswordChangeTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    private const string Password = "Original-Pass-1";

    [E2EFact]
    public async Task A_wrong_current_password_is_refused_and_counts_toward_the_lockout()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var (username, token) = await CreateAndSignInAsync(api);

        var response = await TestApi.WithToken(api, token).PostAsJsonAsync(
            "/api/auth/password", new { currentPassword = "not-my-password", newPassword = "Brand-New-Pass-2" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("current password is wrong", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, FailedLogins(username));
        Assert.Equal(HttpStatusCode.OK, (await TestApi.TryLoginAsync(api, username, Password)).Status);
    }

    [E2EFact]
    public async Task A_weak_or_unchanged_new_password_is_refused()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var (_, token) = await CreateAndSignInAsync(api);
        var client = TestApi.WithToken(api, token);

        var tooShort = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = Password, newPassword = "short" });
        var same = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = Password, newPassword = Password });

        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [E2EFact]
    public async Task Changing_the_password_signs_out_other_sessions_and_keeps_this_one()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var (username, token) = await CreateAndSignInAsync(api);
        var (otherBrowser, _) = await TestApi.TryLoginAsync(api, username, Password);

        var response = await TestApi.WithToken(api, token).PostAsJsonAsync(
            "/api/auth/password", new { currentPassword = Password, newPassword = "Brand-New-Pass-2" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fresh = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await TestApi.WithToken(api, token).GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestApi.WithToken(api, otherBrowser!).GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestApi.WithToken(api, fresh).GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestApi.TryLoginAsync(api, username, "Brand-New-Pass-2")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestApi.TryLoginAsync(api, username, Password)).Status);
    }

    [E2EFact]
    public async Task It_needs_a_signed_in_user()
    {
        await using var api = TestApi.Create(database.ConnectionString);

        var response = await api.CreateClient().PostAsJsonAsync(
            "/api/auth/password", new { currentPassword = "x", newPassword = "Brand-New-Pass-2" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<(string Username, string Token)> CreateAndSignInAsync(WebApplicationFactory<Program> api)
    {
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var username = "pw-" + Guid.NewGuid().ToString("N")[..10];
        var created = await admin.PostAsJsonAsync("/api/users", new { username, password = Password, role = "Guest", sessionLimit = 3 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var (token, status) = await TestApi.TryLoginAsync(api, username, Password);
        Assert.Equal(HttpStatusCode.OK, status);
        return (username, token!);
    }

    private int FailedLogins(string username)
    {
        using var db = database.Open();
        return db.ExecuteScalar<int>("SELECT FailedLoginCount FROM dbo.Users WHERE Username = @username", new { username });
    }
}
