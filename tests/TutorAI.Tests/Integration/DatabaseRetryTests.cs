using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TutorAI.Api.Data;

namespace TutorAI.Tests.Integration;

/// <summary>Opening a connection rides out a database that isn't available yet, as when Azure SQL resumes from a pause.</summary>
[Trait("Category", "Integration")]
public sealed class DatabaseRetryTests
{
    [E2EFact]
    public async Task Opening_a_connection_waits_for_a_database_that_becomes_available()
    {
        // A database that doesn't exist yet fails the open with 4060, a transient error like 40613 in Azure.
        // SqlClient retries a failed login once by itself, 10 s later (ConnectRetryCount 1); the database appears
        // only after that, so this passes only with SqlDatabase's own retries.
        var name = $"TutorAI_E2E_{Guid.NewGuid():N}";
        var server = new SqlConnectionStringBuilder(E2E.ServerConnectionString);
        var database = new SqlDatabase(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TutorAI"] = new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = name }.ConnectionString,
            })
            .Build());
        var master = new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = "master" }.ConnectionString;

        try
        {
            var timer = Stopwatch.StartNew();
            var ping = new SessionRepository(database).PingAsync(CancellationToken.None);

            await Task.Delay(TimeSpan.FromSeconds(15));
            Assert.False(ping.IsCompleted, "The open should still be retrying while the database is missing.");

            await using (var db = new SqlConnection(master))
            {
                await db.ExecuteAsync($"CREATE DATABASE [{name}]");
            }

            Assert.True(await ping.WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(75));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var db = new SqlConnection(master);
            await db.ExecuteAsync(
                $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
        }
    }
}
