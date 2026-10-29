using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace TutorAI.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class HealthTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    [E2EFact]
    public async Task Liveness_answers_without_touching_the_database()
    {
        // A server that doesn't exist: liveness must not care.
        var unreachable = new SqlConnectionStringBuilder(database.ConnectionString)
        {
            DataSource = "localhost,1", ConnectTimeout = 2,
        }.ConnectionString;
        await using var api = TestApi.CreateWithServices(unreachable, _ => { }, ("Auth:AdminPassword", ""));

        var response = await api.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [E2EFact]
    public async Task The_api_starts_and_serves_with_application_insights_configured()
    {
        // A well-formed connection string to an endpoint that doesn't exist: exporting fails quietly in the background.
        await using var api = TestApi.Create(
            database.ConnectionString, null,
            ("APPLICATIONINSIGHTS_CONNECTION_STRING",
             "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost:1/"));
        var client = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sessions")).StatusCode);
        Assert.NotNull(api.Services.GetService(typeof(OpenTelemetry.Trace.TracerProvider)));
    }

    [E2EFact]
    public async Task Readiness_reports_a_wrong_anthropic_key_without_leaking_it()
    {
        const string wrongKey = "sk-ant-api03-not-a-real-key-0000000000";
        await using var api = TestApi.Create(database.ConnectionString, null, ("Anthropic:ApiKey", wrongKey));
        var client = api.CreateClient();

        var response = await client.GetAsync("/api/health/ready");
        var body = await response.Content.ReadAsStringAsync();
        var ready = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(ready.GetProperty("ready").GetBoolean());
        Assert.True(ready.GetProperty("database").GetProperty("ok").GetBoolean());
        Assert.False(ready.GetProperty("anthropic").GetProperty("ok").GetBoolean());
        Assert.DoesNotContain(wrongKey, body);

        // Cached: a second probe within the minute makes no new calls.
        var second = JsonDocument.Parse(await (await client.GetAsync("/api/health/ready")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(ready.GetProperty("checkedAt").GetString(), second.GetProperty("checkedAt").GetString());
    }

    [E2EFact(claude: true, azureSpeech: true)]
    public async Task Readiness_is_200_with_good_credentials()
    {
        await using var api = TestApi.Create(database.ConnectionString);

        var response = await api.CreateClient().GetAsync("/api/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var ready = JsonDocument.Parse(body).RootElement;
        foreach (var dependency in new[] { "database", "anthropic", "speech" })
        {
            Assert.True(ready.GetProperty(dependency).GetProperty("ok").GetBoolean(), body);
        }
    }
}
