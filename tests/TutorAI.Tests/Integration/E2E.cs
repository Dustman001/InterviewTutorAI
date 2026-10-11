using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TutorAI.Migrations;

namespace TutorAI.Tests.Integration;

/// <summary>
/// Integration tests run only with TUTORAI_E2E=1 and need the tutorai-sql container (docker compose up -d).
/// Credentials come from the API's user-secrets or from environment variables.
/// </summary>
internal static class E2E
{
    public static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddUserSecrets<Program>(optional: true)
        .AddEnvironmentVariables()
        .Build();

    public static bool Enabled => Environment.GetEnvironmentVariable("TUTORAI_E2E") == "1";

    public static bool HasClaude =>
        !string.IsNullOrWhiteSpace(Config["Anthropic:ApiKey"]) || !string.IsNullOrWhiteSpace(Config["ANTHROPIC_API_KEY"]);

    public static bool HasAzureSpeech =>
        !string.IsNullOrWhiteSpace(Config["AzureSpeech:Key"]) && !string.IsNullOrWhiteSpace(Config["AzureSpeech:Region"]);

    public static string ServerConnectionString =>
        Config["TUTORAI_E2E_SQL"] ?? Config.GetConnectionString("TutorAI") ?? Migrator.DefaultConnectionString;
}

/// <summary>Skipped unless TUTORAI_E2E=1, and unless the named credentials are present.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute(bool claude = false, bool azureSpeech = false)
    {
        if (!E2E.Enabled)
        {
            Skip = "Set TUTORAI_E2E=1 (and start tutorai-sql) to run integration tests.";
        }
        else if (claude && !E2E.HasClaude)
        {
            Skip = "Needs Anthropic credentials (user-secret Anthropic:ApiKey or ANTHROPIC_API_KEY).";
        }
        else if (azureSpeech && !E2E.HasAzureSpeech)
        {
            Skip = "Needs Azure Speech credentials (user-secrets AzureSpeech:Key and AzureSpeech:Region).";
        }
    }
}

/// <summary>A migrated throwaway database on the tutorai-sql server, dropped when the test class finishes.</summary>
public sealed class ThrowawayDatabase : IDisposable
{
    private readonly Lazy<string> _connectionString;
    private readonly string _name = $"TutorAI_E2E_{Guid.NewGuid():N}";

    public ThrowawayDatabase()
    {
        _connectionString = new Lazy<string>(() =>
        {
            var cs = new SqlConnectionStringBuilder(E2E.ServerConnectionString) { InitialCatalog = _name }.ConnectionString;
            Migrator.Run(cs, quiet: true);
            return cs;
        });
    }

    public string ConnectionString => _connectionString.Value;

    public SqlConnection Open()
    {
        var connection = new SqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        if (!_connectionString.IsValueCreated)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(
            new SqlConnectionStringBuilder(E2E.ServerConnectionString) { InitialCatalog = "master" }.ConnectionString);
        master.Open();
        using var drop = master.CreateCommand();
        drop.CommandText = $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];";
        drop.ExecuteNonQuery();
    }
}
