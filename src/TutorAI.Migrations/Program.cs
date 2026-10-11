using TutorAI.Migrations;

var connectionString = args.FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__TutorAI")
    ?? Migrator.DefaultConnectionString;

try
{
    var applied = Migrator.Run(connectionString);
    Console.WriteLine(applied.Count == 0 ? "Database is up to date." : $"Applied {applied.Count} script(s).");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
