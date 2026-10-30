using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TutorAI.Api.Endpoints;
using TutorAI.Api.Prep;
using TutorAI.Tests.Services;
using static TutorAI.Tests.Services.ScriptedChatClient;

namespace TutorAI.Tests.Integration;

/// <summary>The interview-prep agent through the API against the real database, with a scripted model instead of Claude.</summary>
[Trait("Category", "Integration")]
public sealed class PrepFlowTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    private const string JobDescription =
        "Senior backend engineer: design and run .NET services on Azure, tune SQL Server, lead design reviews and mentor others.";

    private static ScriptedChatClient FullRun(long restartItemId) => new(
        Call(PrepTools.GetWeakAreasTool),
        Call(PrepTools.PlanQuestionsTool, new
        {
            questions = new[]
            {
                new { question = "Tell me about a design review you led.", focus = "behavioural", why = "Leadership." },
                new { question = "How would you find a slow query?", focus = "technical", why = "SQL Server." },
                new { question = "How do you mentor engineers?", focus = "behavioural", why = "Mentoring." },
                new { question = "Why this role?", focus = "motivation", why = "Opener." },
            },
        }),
        Call(PrepTools.AddPracticeItemsTool, new
        {
            phrases = new[]
            {
                new { text = "In my last role I led the design reviews.", explanation = "Opens a leadership answer." },
                new { text = "The outcome was a forty percent faster query.", explanation = "States the result." },
            },
        }),
        Call(PrepTools.ScheduleDrillTool, new { scheduledFor = DateTime.UtcNow.AddDays(1).ToString("O"), durationMinutes = 15, interviewType = "mixed" }),
        Call(PrepTools.RestartPracticeItemTool, new { itemId = restartItemId, reason = "Design reviews need it." }),
        Text("Four questions, two phrases and a drill tomorrow. One restart is waiting for you."));

    [E2EFact]
    public async Task A_run_writes_once_proposes_the_restart_and_its_drill_starts_an_interview()
    {
        var itemId = InsertMasteredItemForAdmin("We should weigh the trade-offs.");
        var chat = FullRun(itemId);
        await using var api = CreateApi(chat);
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/prep/runs")
        {
            Content = JsonContent.Create(new { jobDescription = JobDescription, role = "Backend engineer", seniority = "Senior" }),
        };
        request.Headers.Add(PrepEndpoints.IdempotencyHeader, "run-1");
        var response = await admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal(4, run.GetProperty("questions").GetArrayLength());
        Assert.Equal(6, run.GetProperty("steps").GetInt32());
        var actions = run.GetProperty("actions").EnumerateArray().ToList();
        Assert.Equal(["Applied", "Applied", "PendingConfirmation"], actions.Select(a => a.GetProperty("status").GetString()));
        var drill = Assert.Single(run.GetProperty("drills").EnumerateArray().ToList());
        var runId = run.GetProperty("id").GetGuid();

        // A retry with the same key returns the run without calling the model again.
        using var retry = new HttpRequestMessage(HttpMethod.Post, "/api/prep/runs")
        {
            Content = JsonContent.Create(new { jobDescription = JobDescription }),
        };
        retry.Headers.Add(PrepEndpoints.IdempotencyHeader, "run-1");
        var replay = await admin.SendAsync(retry);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(runId, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(6, chat.Requests.Count);

        // The phrases are practice items now; the mastered item is untouched until confirmed.
        var due = await admin.GetFromJsonAsync<JsonElement>("/api/practice/due");
        Assert.Equal(2, due.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("kind").GetString() == "phrase"));
        Assert.Null(ItemDueAt(itemId));

        // Someone else can't see the run or decide its action.
        var guest = await TestApi.SignInGuestAsync(api);
        var pending = actions[2].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/prep/runs/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsync($"/api/prep/runs/{runId}/actions/{pending}/confirm", null)).StatusCode);

        var confirm = await admin.PostAsync($"/api/prep/runs/{runId}/actions/{pending}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal("Confirmed", (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.NotNull(ItemDueAt(itemId));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/prep/runs/{runId}/actions/{pending}/confirm", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/prep/runs/{runId}/actions/{pending}/reject", null)).StatusCode);
        // The applied writes are not confirmable.
        var applied = actions[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/prep/runs/{runId}/actions/{applied}/confirm", null)).StatusCode);

        // The drill becomes an interview session with the planned questions, once.
        var drillId = drill.GetProperty("id").GetGuid();
        var start = await admin.PostAsync($"/api/prep/drills/{drillId}/start", null);
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
        var again = await admin.PostAsync($"/api/prep/drills/{drillId}/start", null);
        Assert.Equal(sessionId, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid());
        var session = await admin.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}");
        var scenario = session.GetProperty("session").GetProperty("scenario");
        Assert.Equal("Interview", session.GetProperty("session").GetProperty("mode").GetString());
        Assert.Equal(4, scenario.GetProperty("questions").GetArrayLength());

        // The run's tokens show up in the per-user usage.
        using var db = database.Open();
        Assert.Equal(6_000, db.ExecuteScalar<int>("SELECT InputTokens FROM dbo.PrepRuns WHERE Id = @runId", new { runId }));
    }

    [E2EFact]
    public async Task Guests_are_capped_per_day_and_bad_input_is_refused()
    {
        await using var api = CreateApi(new ScriptedChatClient(), ("Prep:RunsPerDay", "1"));
        var guest = await TestApi.SignInGuestAsync(api);
        DeleteGuestRuns();

        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PostAsJsonAsync("/api/prep/runs", new { jobDescription = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await guest.PostAsJsonAsync("/api/prep/runs", new { jobDescription = JobDescription })).StatusCode);
        var capped = await guest.PostAsJsonAsync("/api/prep/runs", new { jobDescription = JobDescription });
        Assert.Equal(HttpStatusCode.TooManyRequests, capped.StatusCode);
        Assert.Contains("1 times a day", (await capped.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    private WebApplicationFactory<Program> CreateApi(ScriptedChatClient chat, params (string Key, string Value)[] settings) =>
        TestApi.CreateWithServices(
            database.ConnectionString,
            services => services.AddSingleton<IPrepChatClientFactory>(new FixedChatClientFactory(chat)),
            [("Prep:RunsPerMinute", "100"), .. settings]);

    private long InsertMasteredItemForAdmin(string target)
    {
        using var db = database.Open();
        // The admin account is created on the API's first startup; make sure it exists by touching the factory first.
        using (var warmup = TestApi.Create(database.ConnectionString))
        {
            warmup.CreateClient();
        }

        return db.ExecuteScalar<long>(
            """
            INSERT INTO dbo.PracticeItems (UserId, Kind, Target, TargetKey, Box, DueAt)
            OUTPUT inserted.Id
            SELECT Id, N'correction', @target, LOWER(@target), 4, NULL FROM dbo.Users WHERE Username = @admin
            """,
            new { target, admin = TestApi.AdminUsername });
    }

    private DateTime? ItemDueAt(long id)
    {
        using var db = database.Open();
        return db.ExecuteScalar<DateTime?>("SELECT DueAt FROM dbo.PracticeItems WHERE Id = @id", new { id });
    }

    private void DeleteGuestRuns()
    {
        using var db = database.Open();
        db.Execute("DELETE r FROM dbo.PrepRuns r JOIN dbo.Users u ON u.Id = r.UserId WHERE u.Username = @guest", new { guest = TestApi.GuestUsername });
    }
}
