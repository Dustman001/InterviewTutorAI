using System.Reflection;
using DbUp;

namespace TutorAI.Migrations;

public static class Migrator
{
    public const string DefaultConnectionString =
        "Server=localhost,14333;Database=TutorAI;User Id=sa;Password=TutorAI!Pass123;TrustServerCertificate=True";

    /// <summary>Creates the database if missing and applies pending scripts. Returns the scripts applied by this run.</summary>
    public static IReadOnlyList<string> Run(string connectionString, bool quiet = false)
    {
        EnsureDatabase.For.SqlDatabase(connectionString);

        var builder = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .JournalToSqlTable("dbo", "SchemaVersions")
            .WithTransactionPerScript();
        builder = quiet ? builder.LogToNowhere() : builder.LogToConsole();

        var result = builder.Build().PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Migration failed at '{result.ErrorScript?.Name}': {result.Error.Message}", result.Error);
        }

        return [.. result.Scripts.Select(s => s.Name)];
    }
}
