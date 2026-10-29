using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TutorAI.Tests.Integration;

/// <summary>
/// The real API on a throwaway database, with a known admin and Key Vault switched off. The guest is an
/// ordinary user created through the admin API (see <see cref="SignInGuestAsync"/>), as in production.
/// </summary>
internal static class TestApi
{
    public const string AdminUsername = "e2e-admin";
    public const string AdminPassword = "e2e-Admin-Pass-1";
    public const string GuestUsername = "e2e-guest";
    public const string GuestPassword = "e2e-Guest-Pass-1";

    public static WebApplicationFactory<Program> Create(string connectionString, string? adminPassword = null) =>
        Create(connectionString, adminPassword, []);

    /// <param name="settings">Extra configuration, e.g. ("Sessions:MaxUserTurns", "2").</param>
    public static WebApplicationFactory<Program> Create(
        string connectionString, string? adminPassword, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder
                .UseSetting("ConnectionStrings:TutorAI", connectionString)
                // Test accounts must not be overridden by secrets in the developer's vault.
                .UseSetting("KeyVault:Uri", "")
                .UseSetting("Auth:JwtSigningKey", "e2e-signing-key-0123456789abcdefghijklmnop")
                .UseSetting("Auth:AdminUsername", AdminUsername)
                .UseSetting("Auth:AdminPassword", adminPassword ?? AdminPassword);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    /// <summary>Signs in and returns the raw token, or null with the status code when sign-in fails.</summary>
    public static async Task<(string? Token, System.Net.HttpStatusCode Status)> TryLoginAsync(
        WebApplicationFactory<Program> api, string username, string password)
    {
        var response = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { username, password });
        return response.IsSuccessStatusCode
            ? ((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString(), response.StatusCode)
            : (null, response.StatusCode);
    }

    /// <summary>Creates the e2e guest (limit 3) through the admin API unless it already exists.</summary>
    public static async Task EnsureGuestAsync(WebApplicationFactory<Program> api)
    {
        var admin = await SignInAsync(api, AdminUsername, AdminPassword);
        var response = await admin.PostAsJsonAsync(
            "/api/users", new { username = GuestUsername, password = GuestPassword, role = "Guest", sessionLimit = 3 });
        Assert.True(
            response.StatusCode is System.Net.HttpStatusCode.Created or System.Net.HttpStatusCode.Conflict,
            await response.Content.ReadAsStringAsync());
    }

    public static async Task<HttpClient> SignInGuestAsync(WebApplicationFactory<Program> api)
    {
        await EnsureGuestAsync(api);
        return await SignInAsync(api, GuestUsername, GuestPassword);
    }

    public static HttpClient WithToken(WebApplicationFactory<Program> api, string token)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<HttpClient> SignInAsync(
        WebApplicationFactory<Program> api, string username, string password, TimeSpan? timeout = null)
    {
        var client = api.CreateClient();
        // HttpClient settings are frozen after the first request, which is the sign-in below.
        if (timeout is { } t) client.Timeout = t;
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
