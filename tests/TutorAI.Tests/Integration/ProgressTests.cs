using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace TutorAI.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class ProgressTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    [E2EFact]
    public async Task Progress_lists_graded_sessions_oldest_first_with_their_measurements()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var guest = await TestApi.SignInGuestAsync(api);
        var guestId = UserId(TestApi.GuestUsername);
        var adminId = UserId(TestApi.AdminUsername);

        var oldest = Seed(guestId, daysAgo: 9, "Conversation", overall: "A2+", wpm: 85.5, composite: 62);
        var middle = Seed(guestId, daysAgo: 5, "Interview", overall: "B1", wpm: 101, composite: null);
        var newest = Seed(guestId, daysAgo: 1, "Conversation", overall: "B1+", wpm: 112.25, composite: 78.5);
        Seed(guestId, daysAgo: 3, "Conversation", overall: null, wpm: 40, composite: null); // too short: not graded
        Seed(adminId, daysAgo: 2, "Conversation", overall: "C1", wpm: 150, composite: 90); // someone else's

        var all = await guest.GetFromJsonAsync<JsonElement>("/api/progress");
        var points = all.GetProperty("points").EnumerateArray().ToList();

        Assert.Equal(TestApi.GuestUsername, all.GetProperty("username").GetString());
        Assert.Equal([oldest, middle, newest], points.Select(p => p.GetProperty("sessionId").GetGuid()));
        Assert.Equal(["A2+", "B1", "B1+"], points.Select(p => p.GetProperty("overall").GetString()));
        Assert.Equal(112.25, points[2].GetProperty("wordsPerMinute").GetDouble());
        Assert.Equal(78.5, points[2].GetProperty("pronunciationScore").GetDouble());
        Assert.Equal(JsonValueKind.Null, points[1].GetProperty("pronunciationScore").ValueKind);

        var conversations = await guest.GetFromJsonAsync<JsonElement>("/api/progress?mode=Conversation");
        Assert.Equal(
            [oldest, newest],
            conversations.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("sessionId").GetGuid()));
    }

    [E2EFact]
    public async Task Only_admins_can_see_someone_elses_progress()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var guest = await TestApi.SignInGuestAsync(api);
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var guestId = UserId(TestApi.GuestUsername);
        var adminId = UserId(TestApi.AdminUsername);

        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync($"/api/progress?userId={adminId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/progress?userId={guestId}")).StatusCode);

        var asAdmin = await admin.GetFromJsonAsync<JsonElement>($"/api/progress?userId={guestId}");
        Assert.Equal(TestApi.GuestUsername, asAdmin.GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/progress?userId=999999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/progress?mode=Karaoke")).StatusCode);
    }

    private Guid Seed(int userId, int daysAgo, string mode, string? overall, double wpm, double? composite)
    {
        var id = Guid.NewGuid();
        var metrics = JsonSerializer.Serialize(new
        {
            fluency = new { totalWords = 300, wordsPerMinute = wpm, pauseRatio = 0.2 },
            pronunciation = new { composite },
        });
        using var db = database.Open();
        db.Execute(
            """
            INSERT INTO dbo.Sessions (Id, Mode, ScenarioJson, StartedAt, Status, UserId, GradingState)
            VALUES (@id, @mode, '{}', DATEADD(day, -@daysAgo, SYSUTCDATETIME()), 'Ended', @userId, 'Done');
            INSERT INTO dbo.Assessments (SessionId, Fluency, Grammar, Vocabulary, Interaction, Pronunciation, Overall, MetricsJson, DetailJson)
            VALUES (@id, @overall, @overall, @overall, @overall, NULL, @overall, @metrics, '{}');
            """,
            new { id, mode, daysAgo, userId, overall, metrics });
        return id;
    }

    private int UserId(string username)
    {
        using var db = database.Open();
        return db.ExecuteScalar<int>("SELECT Id FROM dbo.Users WHERE Username = @username", new { username });
    }
}
