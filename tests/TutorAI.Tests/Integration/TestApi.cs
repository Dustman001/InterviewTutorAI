using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TutorAI.Tests.Integration;

/// <summary>The real API on a throwaway database, with known sign-in accounts and Key Vault switched off.</summary>
internal static class TestApi
{
    public const string AdminUsername = "e2e-admin";
    public const string AdminPassword = "e2e-Admin-Pass-1";
    public const string GuestUsername = "e2e-guest";
    public const string GuestPassword = "e2e-Guest-Pass-1";

    public static WebApplicationFactory<Program> Create(string connectionString, string? adminPassword = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:TutorAI", connectionString)
            // Test accounts must not be overridden by secrets in the developer's vault.
            .UseSetting("KeyVault:Uri", "")
            .UseSetting("Auth:JwtSigningKey", "e2e-signing-key-0123456789abcdefghijklmnop")
            .UseSetting("Auth:AdminUsername", AdminUsername)
            .UseSetting("Auth:AdminPassword", adminPassword ?? AdminPassword)
            .UseSetting("Auth:GuestUsername", GuestUsername)
            .UseSetting("Auth:GuestPassword", GuestPassword)
            .UseSetting("Auth:GuestSessionLimit", "3"));

    /// <summary>Signs in and returns the raw token, or null with the status code when sign-in fails.</summary>
    public static async Task<(string? Token, System.Net.HttpStatusCode Status)> TryLoginAsync(
        WebApplicationFactory<Program> api, string username, string password)
    {
        var response = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { username, password });
        return response.IsSuccessStatusCode
            ? ((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString(), response.StatusCode)
            : (null, response.StatusCode);
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
