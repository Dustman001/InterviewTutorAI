using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace TutorAI.Tests.Integration;

/// <summary>The MCP server at /mcp, driven by the SDK's own client over the in-process API and the real database.</summary>
[Trait("Category", "Integration")]
public sealed class McpServerTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    [E2EFact]
    public async Task A_client_lists_the_tools_and_reads_only_its_own_data()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var guest = await TestApi.SignInGuestAsync(api);
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var guestSession = await CreateSessionAsync(guest);
        var adminSession = await CreateSessionAsync(admin);
        InsertPracticeItem(TestApi.GuestUsername, "throughput");

        await using var client = await ConnectAsync(api, await McpTokenAsync(guest));

        var tools = await client.ListToolsAsync();
        Assert.Equal(["get_progress", "get_session", "list_practice_items", "list_sessions"], tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));

        var sessions = Json(await client.CallToolAsync("list_sessions", new Dictionary<string, object?> { ["limit"] = 10 }));
        var ids = sessions.EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(guestSession, ids);
        Assert.DoesNotContain(adminSession, ids);

        var practice = Json(await client.CallToolAsync("list_practice_items"));
        Assert.Equal("throughput", Assert.Single(practice.GetProperty("items").EnumerateArray().ToList()).GetProperty("target").GetString());

        var own = Json(await client.CallToolAsync("get_session", new Dictionary<string, object?> { ["sessionId"] = guestSession }));
        Assert.Equal(guestSession, own.GetProperty("id").GetGuid());

        var others = await client.CallToolAsync("get_session", new Dictionary<string, object?> { ["sessionId"] = adminSession });
        Assert.True(others.IsError);
        Assert.Contains("No such session", Text(others));

        Assert.Equal(JsonValueKind.Array, Json(await client.CallToolAsync("get_progress")).ValueKind);
    }

    [E2EFact]
    public async Task Web_and_mcp_tokens_only_work_where_they_belong_and_a_password_change_revokes()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        // Its own user, so the password change below doesn't affect the other tests.
        const string username = "mcp-revoke";
        const string password = "mcp-Revoke-Pass-1";
        var admin = await TestApi.SignInAsync(api, TestApi.AdminUsername, TestApi.AdminPassword);
        var created = await admin.PostAsJsonAsync("/api/users", new { username, password, role = "Guest", sessionLimit = 1 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var user = await TestApi.SignInAsync(api, username, password);
        var webToken = user.DefaultRequestHeaders.Authorization!.Parameter!;
        var mcpToken = await McpTokenAsync(user);

        // A web token can't open /mcp.
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeStatusAsync(api, webToken));
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(api, webToken));

        // An MCP token can't call the API, nor be swapped for a web token.
        var withMcp = TestApi.WithToken(api, mcpToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withMcp.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withMcp.PostAsync("/api/auth/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withMcp.PostAsync("/api/auth/mcp-token", null)).StatusCode);

        await using (var client = await ConnectAsync(api, mcpToken))
        {
            Assert.NotEmpty(await client.ListToolsAsync());
        }

        // Changing the password signs out everywhere, MCP clients included.
        var changed = await user.PostAsJsonAsync("/api/auth/password", new { currentPassword = password, newPassword = password + "x" });
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeStatusAsync(api, mcpToken));
    }

    /// <summary>The status of a raw MCP initialize request, to tell an auth refusal from any other failure.</summary>
    private static async Task<HttpStatusCode> InitializeStatusAsync(WebApplicationFactory<Program> api, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return (await api.CreateClient().SendAsync(request)).StatusCode;
    }

    private static async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> api, string token)
    {
        var http = api.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            },
            http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<string> McpTokenAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/auth/mcp-token", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/mcp", body.GetProperty("path").GetString());
        Assert.True(body.GetProperty("expiresAt").GetDateTime() > DateTime.UtcNow.AddDays(29));
        return body.GetProperty("token").GetString()!;
    }

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/sessions", new { mode = "Conversation", scenario = new { topic = "Travel" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private void InsertPracticeItem(string username, string target)
    {
        using var db = database.Open();
        db.Execute(
            """
            INSERT INTO dbo.PracticeItems (UserId, Kind, Target, TargetKey, Box, DueAt)
            SELECT Id, N'word', @target, @target, 0, SYSUTCDATETIME() FROM dbo.Users WHERE Username = @username
            """,
            new { target, username });
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static JsonElement Json(CallToolResult result)
    {
        Assert.True(result.IsError is not true, Text(result));
        return JsonSerializer.Deserialize<JsonElement>(Text(result));
    }
}
