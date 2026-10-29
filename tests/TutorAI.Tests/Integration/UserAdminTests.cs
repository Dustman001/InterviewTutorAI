using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TutorAI.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class UserAdminTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    [E2EFact]
    public async Task Only_admins_can_manage_users()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var guest = await TestApi.SignInGuestAsync(api);

        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/users")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await guest.PostAsJsonAsync("/api/users", NewUser("sneaky", "Admin", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/users")).StatusCode);
    }

    [E2EFact]
    public async Task Admin_creates_a_user_who_can_sign_in_and_validation_applies()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var admin = await AdminAsync(api);

        var created = await admin.PostAsJsonAsync("/api/users", NewUser("maria", "Guest", 5));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var user = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, user.GetProperty("sessionLimit").GetInt32());

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/users", NewUser("maria", "Guest", 5))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", NewUser("x", "Guest", 3))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/users", new { username = "shortpw", password = "123", role = "Guest", sessionLimit = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", NewUser("norole", "Owner", 3))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", NewUser("nolimit", "Guest", null))).StatusCode);

        var maria = await TestApi.SignInAsync(api, "maria", Password);
        var me = await maria.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("Guest", me.GetProperty("role").GetString());
        Assert.Equal(5, me.GetProperty("sessionLimit").GetInt32());

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        Assert.Contains(list.EnumerateArray(), u => u.GetProperty("username").GetString() == "maria");
    }

    [E2EFact]
    public async Task Disabling_a_user_cuts_off_their_token_at_once_and_blocks_sign_in()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var admin = await AdminAsync(api);
        var id = await CreateAsync(admin, "disable-me", "Guest", 3);
        var user = await TestApi.SignInAsync(api, "disable-me", Password);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/sessions")).StatusCode);

        await PutAsync(admin, id, new { isDisabled = true });

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TestApi.TryLoginAsync(api, "disable-me", Password)).Status);

        await PutAsync(admin, id, new { isDisabled = false });
        Assert.Equal(HttpStatusCode.OK, (await TestApi.TryLoginAsync(api, "disable-me", Password)).Status);
    }

    [E2EFact]
    public async Task Password_reset_signs_the_user_out_and_only_the_new_password_works()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var admin = await AdminAsync(api);
        var id = await CreateAsync(admin, "reset-me", "Guest", 3);
        var oldSession = await TestApi.SignInAsync(api, "reset-me", Password);

        var reset = await admin.PostAsJsonAsync($"/api/users/{id}/password", new { password = "brand-new-pass" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestApi.TryLoginAsync(api, "reset-me", Password)).Status);
        Assert.Equal(HttpStatusCode.OK, (await TestApi.TryLoginAsync(api, "reset-me", "brand-new-pass")).Status);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/users/{id}/password", new { password = "short" })).StatusCode);
    }

    [E2EFact]
    public async Task Resetting_sessions_restores_the_allowance_and_keeps_history()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var admin = await AdminAsync(api);
        var id = await CreateAsync(admin, "limited", "Guest", 1);
        var user = await TestApi.SignInAsync(api, "limited", Password);

        Assert.Equal(HttpStatusCode.Created, (await StartAsync(user)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartAsync(user)).StatusCode);

        var reset = await admin.PostAsync($"/api/users/{id}/reset-sessions", null);
        Assert.Equal(0, (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionsUsed").GetInt32());

        Assert.Equal(HttpStatusCode.Created, (await StartAsync(user)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartAsync(user)).StatusCode);
        var history = (await user.GetFromJsonAsync<JsonElement>("/api/sessions")).GetProperty("items");
        Assert.Equal(2, history.GetArrayLength());
    }

    [E2EFact]
    public async Task Admins_cannot_lock_themselves_or_everyone_out()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var admin = await AdminAsync(api);
        var selfId = await IdOfAsync(admin, TestApi.AdminUsername);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/users/{selfId}", new { isDisabled = true })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync($"/api/users/{selfId}", new { role = "Guest", sessionLimit = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/users/{selfId}")).StatusCode);

        // Demoting another admin is fine while an enabled admin remains, and it cuts off their admin token.
        var otherId = await CreateAsync(admin, "second-admin", "Admin", null);
        var other = await TestApi.SignInAsync(api, "second-admin", Password);
        await PutAsync(admin, otherId, new { role = "Guest", sessionLimit = 3 });
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/users")).StatusCode);
    }

    [E2EFact]
    public async Task Deleting_a_user_keeps_their_sessions_for_admins()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var admin = await AdminAsync(api);
        var id = await CreateAsync(admin, "leaving", "Guest", 3);
        var user = await TestApi.SignInAsync(api, "leaving", Password);
        var session = (await (await StartAsync(user)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/sessions/{session}")).StatusCode);
        using var db = database.Open();
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.Users WHERE Username = 'leaving'"));
    }

    [E2EFact]
    public async Task Tokens_last_fifteen_minutes_and_refresh_issues_a_new_one()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        await TestApi.EnsureGuestAsync(api);
        var login = await api.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { username = TestApi.GuestUsername, password = TestApi.GuestPassword });
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var lifetime = body.GetProperty("expiresAt").GetDateTime().ToUniversalTime() - DateTime.UtcNow;
        Assert.InRange(lifetime.TotalMinutes, 14, 15.1);

        var client = TestApi.WithToken(api, body.GetProperty("token").GetString()!);
        var refreshed = await client.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var newToken = (await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await TestApi.WithToken(api, newToken).GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [E2EFact]
    public async Task A_password_set_on_the_page_survives_restarts_until_the_configured_one_changes()
    {
        // Its own accounts, so other tests' admin changes don't interfere.
        await using (var first = TestApi.Create(database.ConnectionString))
        {
            await TestApi.EnsureGuestAsync(first);
            var admin = await AdminAsync(first);
            var guestId = await IdOfAsync(admin, TestApi.GuestUsername);
            await admin.PostAsJsonAsync($"/api/users/{guestId}/password", new { password = "set-on-the-page" });
        }

        await using (var restarted = TestApi.Create(database.ConnectionString))
        {
            Assert.Equal(HttpStatusCode.OK, (await TestApi.TryLoginAsync(restarted, TestApi.GuestUsername, "set-on-the-page")).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await TestApi.TryLoginAsync(restarted, TestApi.GuestUsername, TestApi.GuestPassword)).Status);
        }

        // Changing the configured admin password and restarting applies it (recovery path).
        await using var withNewConfig = TestApi.Create(database.ConnectionString, adminPassword: "rotated-admin-pass");
        Assert.Equal(HttpStatusCode.OK, (await TestApi.TryLoginAsync(withNewConfig, TestApi.AdminUsername, "rotated-admin-pass")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestApi.TryLoginAsync(withNewConfig, TestApi.AdminUsername, TestApi.AdminPassword)).Status);

        // Put the shared accounts back for the other tests in this class.
        var admin2 = await TestApi.SignInAsync(withNewConfig, TestApi.AdminUsername, "rotated-admin-pass");
        await admin2.PostAsJsonAsync($"/api/users/{await IdOfAsync(admin2, TestApi.GuestUsername)}/password", new { password = TestApi.GuestPassword });
        await admin2.PostAsJsonAsync($"/api/users/{await IdOfAsync(admin2, TestApi.AdminUsername)}/password", new { password = TestApi.AdminPassword });
    }

    private const string Password = "test-pass-123";

    private static object NewUser(string username, string role, int? limit) =>
        new { username, password = Password, role, sessionLimit = limit };

    private static Task<HttpClient> AdminAsync(WebApplicationFactory<Program> api) =>
        TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);

    private static async Task<int> CreateAsync(HttpClient admin, string username, string role, int? limit)
    {
        var response = await admin.PostAsJsonAsync("/api/users", NewUser(username, role, limit));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task PutAsync(HttpClient admin, int id, object body)
    {
        var response = await admin.PutAsJsonAsync($"/api/users/{id}", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<int> IdOfAsync(HttpClient admin, string username) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/users")).EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == username).GetProperty("id").GetInt32();

    private static Task<HttpResponseMessage> StartAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/sessions", new { mode = "Conversation", scenario = new { topic = "Travel", durationMinutes = 5 } });
}
