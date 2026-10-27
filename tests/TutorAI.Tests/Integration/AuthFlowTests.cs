using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace TutorAI.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class AuthFlowTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    [E2EFact]
    public async Task Endpoints_need_a_valid_token_except_health_and_login()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/speech/token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/sessions", new { mode = "Conversation" })).StatusCode);

        var wrong = await anonymous.PostAsJsonAsync(
            "/api/auth/login", new { username = TestApi.AdminUsername, password = "not-the-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var forged = api.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            // Same claims shape, signed with a different key.
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIiwibmFtZSI6ImUyZS1hZG1pbiIsInJvbGUiOiJBZG1pbiIsImlzcyI6IlR1dG9yQUkuQXBpIiwiYXVkIjoiVHV0b3JBSS5XZWIiLCJleHAiOjQxMDI0NDQ4MDB9.c2lnbmVkLXdpdGgtYW5vdGhlci1rZXk");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/sessions")).StatusCode);
    }

    [E2EFact]
    public async Task Guest_gets_three_sessions_and_sees_only_their_own()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var guest = await TestApi.SignInGuestAsync(api);
        var adminSession = await CreateSessionAsync(admin);

        var me = await guest.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("Guest", me.GetProperty("role").GetString());
        Assert.Equal(3, me.GetProperty("sessionLimit").GetInt32());
        var usedBefore = me.GetProperty("sessionsUsed").GetInt32();

        var guestSessions = new List<Guid>();
        for (var i = usedBefore; i < 3; i++)
        {
            guestSessions.Add(await CreateSessionAsync(guest));
        }

        var fourth = await guest.PostAsJsonAsync("/api/sessions", new { mode = "Conversation" });
        Assert.Equal(HttpStatusCode.Forbidden, fourth.StatusCode);
        Assert.Contains("3 sessions", await fourth.Content.ReadAsStringAsync());
        Assert.Equal(3, (await guest.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("sessionsUsed").GetInt32());

        // The guest cannot see, continue or end the admin's session, and does not list it.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/sessions/{adminSession}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.PostAsJsonAsync($"/api/sessions/{adminSession}/turns", new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsync($"/api/sessions/{adminSession}/end", null)).StatusCode);
        var guestList = await guest.GetFromJsonAsync<JsonElement>("/api/sessions");
        Assert.DoesNotContain(guestList.EnumerateArray(), s => s.GetProperty("id").GetGuid() == adminSession);
        Assert.All(guestSessions, id => Assert.Contains(guestList.EnumerateArray(), s => s.GetProperty("id").GetGuid() == id));

        // The admin sees everyone's sessions, including the guest's.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/sessions/{guestSessions[0]}")).StatusCode);

        using var db = database.Open();
        Assert.Equal(
            3,
            db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.Sessions s JOIN dbo.Users u ON u.Id = s.UserId WHERE u.Username = @name",
                new { name = TestApi.GuestUsername }));
    }

    [E2EFact]
    public async Task Admin_has_no_session_limit()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);

        for (var i = 0; i < 5; i++)
        {
            await CreateSessionAsync(admin);
        }

        var me = await admin.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("Admin", me.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("sessionLimit").ValueKind);
        Assert.True(me.GetProperty("sessionsUsed").GetInt32() >= 5);
    }

    [E2EFact]
    public async Task Passwords_are_stored_hashed()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        await TestApi.SignInGuestAsync(api);

        using var db = database.Open();
        var hash = db.ExecuteScalar<string>(
            "SELECT PasswordHash FROM dbo.Users WHERE Username = @name", new { name = TestApi.GuestUsername });
        Assert.DoesNotContain(TestApi.GuestPassword, hash);
        Assert.True(hash!.Length > 40);
    }

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sessions", new { mode = "Conversation", scenario = new { topic = "Travel", durationMinutes = 5 } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
