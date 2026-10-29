using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using TutorAI.Api.Data;

namespace TutorAI.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class SessionPagingTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    [E2EFact]
    public async Task Sessions_page_newest_first_without_gaps_or_repeats_even_with_inserts_between_pages()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        // A guest lists only their own sessions, so nothing else in this database lands in the pages.
        var guest = await TestApi.SignInGuestAsync(api);
        var userId = UserId(TestApi.GuestUsername);
        // 120 sessions; groups of three share a start time, so the Id tie-break matters.
        var seeded = SeedSessions(userId, count: 120, sameTimePerGroup: 3);

        var first = await PageAsync(guest, null);
        // Newer sessions arrive after the first page has been read: they must not shift later pages.
        SeedSessions(userId, count: 5, sameTimePerGroup: 1, startOffsetMinutes: 10);
        var second = await PageAsync(guest, first.NextCursor);
        var third = await PageAsync(guest, second.NextCursor);

        Assert.Equal((50, 50, 20), (first.Ids.Count, second.Ids.Count, third.Ids.Count));
        Assert.Null(third.NextCursor);
        var all = first.Ids.Concat(second.Ids).Concat(third.Ids).ToList();
        Assert.Equal(seeded, all);
    }

    [E2EFact]
    public async Task Page_size_is_clamped_and_bad_cursors_are_refused()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        SeedSessions(UserId(TestApi.AdminUsername), count: 3, sameTimePerGroup: 1, startOffsetMinutes: -5000);

        var one = await admin.GetFromJsonAsync<JsonElement>("/api/sessions?limit=1");
        var bad = await admin.GetAsync("/api/sessions?before=not-a-cursor");

        Assert.Equal(1, one.GetProperty("items").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, one.GetProperty("nextCursor").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [E2EFact]
    public void The_list_queries_seek_on_the_new_indexes()
    {
        using var db = database.Open();
        foreach (var (filter, index) in new[]
        {
            ("WHERE s.UserId = 1 AND s.StartedAt <= SYSUTCDATETIME()", "IX_Sessions_UserId_StartedAt"),
            ("WHERE s.StartedAt <= SYSUTCDATETIME()", "IX_Sessions_StartedAt"),
        })
        {
            db.Execute("SET SHOWPLAN_XML ON");
            var plan = db.QuerySingle<string>(
                $"SELECT TOP (51) s.Id, s.StartedAt, s.Status FROM dbo.Sessions s {filter} ORDER BY s.StartedAt DESC, s.Id DESC");
            db.Execute("SET SHOWPLAN_XML OFF");

            Assert.Contains(index, plan);
            Assert.Contains("<SeekPredicates>", plan);
        }
    }

    private static async Task<(List<Guid> Ids, string? NextCursor)> PageAsync(HttpClient client, string? before)
    {
        var url = before is null ? "/api/sessions" : $"/api/sessions?before={Uri.EscapeDataString(before)}";
        var page = await client.GetFromJsonAsync<JsonElement>(url);
        return (
            [.. page.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetGuid())],
            page.GetProperty("nextCursor").GetString());
    }

    /// <summary>Inserts sessions directly and returns their ids in the order the list should show them.</summary>
    private List<Guid> SeedSessions(int userId, int count, int sameTimePerGroup, int startOffsetMinutes = -1000)
    {
        using var db = database.Open();
        var start = db.ExecuteScalar<DateTime>("SELECT DATEADD(minute, @startOffsetMinutes, SYSUTCDATETIME())", new { startOffsetMinutes });
        for (var i = 0; i < count; i++)
        {
            var startedAt = start.AddSeconds(i / sameTimePerGroup);
            var p = new DynamicParameters(new { userId, id = Guid.NewGuid() });
            p.Add("startedAt", startedAt, System.Data.DbType.DateTime2);
            db.Execute(
                "INSERT INTO dbo.Sessions (Id, Mode, ScenarioJson, StartedAt, Status, UserId) VALUES (@id, 'Conversation', '{}', @startedAt, 'Ended', @userId)",
                p);
        }

        // The expected order is the database's own: newest first, then Id descending in SQL Server's ordering.
        return [.. db.Query<Guid>(
            "SELECT Id FROM dbo.Sessions WHERE UserId = @userId AND StartedAt >= @start ORDER BY StartedAt DESC, Id DESC",
            new { userId, start })];
    }

    private int UserId(string username)
    {
        using var db = database.Open();
        return db.ExecuteScalar<int>("SELECT Id FROM dbo.Users WHERE Username = @username", new { username });
    }
}
