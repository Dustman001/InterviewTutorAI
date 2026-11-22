using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TutorAI.Tests.Logging;

/// <summary>One captured log call: its structured state and the scopes active when it was written.</summary>
internal sealed record LogEntry(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> State,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Scopes)
{
    public object? this[string key] => State.TryGetValue(key, out var value) ? value : null;

    /// <summary>Everything a reader of the log would see, for "this text never appears" checks.</summary>
    public string AllText() =>
        string.Join(
            "\n",
            new[] { Message, Exception?.ToString() ?? "" }
                .Concat(State.Values.Select(v => v?.ToString() ?? ""))
                .Concat(Scopes.Values.Select(v => v?.ToString() ?? "")));
}

/// <summary>Records every log call at every level, for tests that assert what was logged.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public IReadOnlyList<LogEntry> From(string categoryPrefix) =>
        Entries.Where(e => e.Category.StartsWith(categoryPrefix, StringComparison.Ordinal)).ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    values[key] = value;
                }
            }

            var scopes = new Dictionary<string, object?>();
            provider._scopes.ForEachScope(
                (scope, target) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> scopePairs)
                    {
                        foreach (var (key, value) in scopePairs)
                        {
                            target[key] = value;
                        }
                    }
                },
                scopes);

            provider._entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), values, exception, scopes));
        }
    }
}

internal static class CapturingLoggerExtensions
{
    /// <summary>
    /// Adds the provider to a host and lets every level through for the categories the tests read, overriding the
    /// appsettings filters (Microsoft.AspNetCore is Warning there).
    /// </summary>
    public static IServiceCollection AddCapturingLogger(this IServiceCollection services, CapturingLoggerProvider provider)
    {
        services.AddSingleton<ILoggerProvider>(provider);
        services.Configure<LoggerFilterOptions>(options =>
        {
            options.Rules.Add(new LoggerFilterRule(null, "TutorAI", LogLevel.Trace, null));
            options.Rules.Add(new LoggerFilterRule(null, "Microsoft.AspNetCore.Diagnostics", LogLevel.Trace, null));
        });
        return services;
    }
}
