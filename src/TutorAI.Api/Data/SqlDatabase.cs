using System.Data;
using System.Data.Common;
using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;

namespace TutorAI.Api.Data;

/// <summary>Writes that must succeed or fail together, across repositories. Dispose without committing rolls back.</summary>
public interface IUnitOfWork : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}

public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> BeginAsync(CancellationToken ct);
}

/// <summary>Opens connections to the TutorAI database and starts transactions shared by repositories.</summary>
public sealed class SqlDatabase : IUnitOfWorkFactory
{
    private static readonly Lock DapperGate = new();
    private static bool _dapperConfigured;

    private readonly string _connectionString;

    public SqlDatabase(IConfiguration configuration)
    {
        ConfigureDapper();
        _connectionString = configuration.GetConnectionString("TutorAI")
            ?? throw new InvalidOperationException("ConnectionStrings:TutorAI is not configured.");
    }

    public SqlConnection Open() => new(_connectionString);

    public async Task<IUnitOfWork> BeginAsync(CancellationToken ct)
    {
        var connection = Open();
        try
        {
            await connection.OpenAsync(ct);
            return new SqlUnitOfWork(connection, await connection.BeginTransactionAsync(ct));
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Process-wide Dapper settings. Safe to call more than once.</summary>
    public static void ConfigureDapper()
    {
        lock (DapperGate)
        {
            if (_dapperConfigured)
            {
                return;
            }

            // Dapper's default for DateTime is datetime, which rounds to ~3 ms; the columns are datetime2.
            SqlMapper.AddTypeMap(typeof(DateTime), DbType.DateTime2);
            SqlMapper.AddTypeMap(typeof(DateTime?), DbType.DateTime2);
            // Every IStringValue type in the API, wherever it's declared, is stored as its text.
            var addHandler = typeof(StringValue).GetMethod(nameof(StringValue.AddDapperHandler), BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var type in typeof(SqlDatabase).Assembly.GetTypes().Where(t =>
                         t.IsValueType && t.GetInterfaces().Any(i =>
                             i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStringValue<>) && i.GetGenericArguments()[0] == t)))
            {
                addHandler.MakeGenericMethod(type).Invoke(null, null);
            }

            _dapperConfigured = true;
        }
    }
}

public sealed class SqlUnitOfWork(SqlConnection connection, DbTransaction transaction) : IUnitOfWork
{
    public SqlConnection Connection { get; } = connection;
    public DbTransaction Transaction { get; } = transaction;

    public Task CommitAsync(CancellationToken ct) => Transaction.CommitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

internal static class UnitOfWorkExtensions
{
    /// <summary>The SQL transaction behind a unit of work from <see cref="SqlDatabase"/>.</summary>
    public static SqlUnitOfWork Sql(this IUnitOfWork work) =>
        work as SqlUnitOfWork ?? throw new ArgumentException($"Expected a unit of work from {nameof(SqlDatabase)}.", nameof(work));
}
