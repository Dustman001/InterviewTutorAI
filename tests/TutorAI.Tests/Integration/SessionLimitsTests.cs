using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TutorAI.Api.Data;

namespace TutorAI.Tests.Integration;

/// <summary>Every refusal here happens before the Claude call, so these tests need only the database.</summary>
[Trait("Category", "Integration")]
public sealed class SessionLimitsTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    [E2EFact]
    public async Task Out_of_range_scenarios_are_refused_on_create()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var client = await SignInAsync(api);

        var tooLong = await client.PostAsJsonAsync(
            "/api/sessions", new { mode = "Conversation", scenario = new { topic = "Travel", durationMinutes = 100_000 } });
        var hugeDescription = await client.PostAsJsonAsync(
            "/api/sessions",
            new { mode = "Interview", scenario = new { role = "Engineer", jobDescription = new string('x', 9000), durationMinutes = 10 } });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("minutes", await tooLong.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, hugeDescription.StatusCode);
    }

    [E2EFact]
    public async Task Oversized_turns_and_bodies_are_refused()
    {
        await using var api = TestApi.Create(database.ConnectionString, null, ("Sessions:MaxRequestBytes", "50000"));
        var client = await SignInAsync(api);
        var sessionId = await CreateSessionAsync(client);

        var longText = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", new { text = new string('x', 4001) });
        Assert.Equal(HttpStatusCode.BadRequest, longText.StatusCode);
        Assert.Equal("turn_too_long", await CodeAsync(longText));

        var body = new StringContent(JsonSerializer.Serialize(new { text = "Hi", padding = new string('x', 60_000) }), Encoding.UTF8, "application/json");
        var huge = await client.PostAsync($"/api/sessions/{sessionId}/turns", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);

        AssertNoTurnsStored(sessionId);
    }

    [E2EFact]
    public async Task Session_stops_taking_turns_at_the_turn_limit()
    {
        await using var api = TestApi.Create(database.ConnectionString, null, ("Sessions:MaxUserTurns", "2"));
        var client = await SignInAsync(api);
        var sessionId = await CreateSessionAsync(client);
        var repository = api.Services.GetRequiredService<ISessionRepository>();
        foreach (var text in new[] { "First.", "Second." })
        {
            await repository.AddTurnAsync(new TurnRow { SessionId = sessionId, Role = TurnRole.User, Text = text, WordCount = 1 }, CancellationToken.None);
        }

        var third = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", new { text = "Third." });

        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Equal("turn_limit", await CodeAsync(third));
    }

    [E2EFact]
    public async Task Session_stops_taking_turns_after_the_planned_time_plus_grace()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var client = await SignInAsync(api);
        var sessionId = await CreateSessionAsync(client);
        using (var db = database.Open())
        {
            // A 10-minute session started 16 minutes ago: past the 5-minute grace.
            db.Execute("UPDATE dbo.Sessions SET StartedAt = DATEADD(minute, -16, SYSUTCDATETIME()) WHERE Id = @sessionId", new { sessionId });
        }

        var opening = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", new { });
        var turn = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", new { text = "Hello?" });

        Assert.Equal(HttpStatusCode.Conflict, opening.StatusCode);
        Assert.Equal("time_up", await CodeAsync(opening));
        Assert.Equal("time_up", await CodeAsync(turn));
        AssertNoTurnsStored(sessionId);
    }

    [E2EFact]
    public async Task Turn_requests_are_rate_limited_per_user()
    {
        await using var api = TestApi.Create(database.ConnectionString, null, ("Sessions:TurnsPerMinute", "3"));
        var admin = await SignInAsync(api);
        var guest = await TestApi.SignInGuestAsync(api);
        var sessionId = await CreateSessionAsync(admin);
        // An ended session answers 409 without calling Claude; the requests still count against the limit.
        Assert.True((await admin.PostAsync($"/api/sessions/{sessionId}/end", null)).IsSuccessStatusCode);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await admin.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", new { text = "Hi." })).StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.Conflict, HttpStatusCode.Conflict, HttpStatusCode.Conflict, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            statuses);
        // Another user has their own allowance (404: the guest can't see the admin's session).
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync($"/api/sessions/{sessionId}/turns", new { text = "Hi." })).StatusCode);
    }

    private static Task<HttpClient> SignInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api) =>
        TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/sessions", new { mode = "Conversation", scenario = new { topic = "Travel", durationMinutes = 10 } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private void AssertNoTurnsStored(Guid sessionId)
    {
        using var db = database.Open();
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.Turns WHERE SessionId = @sessionId", new { sessionId }));
    }
}
