using System.Diagnostics;
using System.Diagnostics.Metrics;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;

namespace TutorAI.Api;

/// <summary>
/// Spans and metrics for the parts App Service can't see: Claude calls, grading and sign-in failures.
/// Exported to Application Insights when APPLICATIONINSIGHTS_CONNECTION_STRING is set (see Program.cs).
/// </summary>
public static class Telemetry
{
    public const string Name = "TutorAI";

    public const string ChatRoute = "chat";
    public const string GradeRoute = "grade";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    private static readonly Counter<long> ClaudeTokens = Meter.CreateCounter<long>(
        "tutorai.claude.tokens", "{token}", "Claude tokens, tagged by model, kind (input, output, cache_read, cache_write) and route (chat, grade).");

    public static readonly Histogram<double> TurnFirstTokenMs = Meter.CreateHistogram<double>(
        "tutorai.turn.first_token_ms", "ms", "Time from sending a tutor turn request to Claude's first text.");

    public static readonly Histogram<double> GradingDurationMs = Meter.CreateHistogram<double>(
        "tutorai.grading.duration_ms", "ms", "Time to grade a session, per attempt.");

    public static readonly Counter<long> GradingFailures = Meter.CreateCounter<long>(
        "tutorai.grading.failures", "{attempt}", "Failed grading attempts, tagged final=true when the session was marked Failed.");

    public static readonly Counter<long> LoginsFailed = Meter.CreateCounter<long>(
        "tutorai.logins.failed", "{attempt}", "Failed sign-ins, tagged by reason (password, locked, unknown_user).");

    public static void RecordUsage(Activity? activity, string route, ClaudeUsage usage)
    {
        activity?.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
        activity?.SetTag("tutorai.cache_read_tokens", usage.CacheReadTokens);
        activity?.SetTag("tutorai.cache_write_tokens", usage.CacheWriteTokens);

        Add(usage.InputTokens, "input");
        Add(usage.OutputTokens, "output");
        Add(usage.CacheReadTokens, "cache_read");
        Add(usage.CacheWriteTokens, "cache_write");

        void Add(long tokens, string kind)
        {
            if (tokens > 0)
            {
                ClaudeTokens.Add(
                    tokens, new("model", usage.Model), new("kind", kind), new("route", route));
            }
        }
    }

    /// <summary>Starts a span for one Claude call.</summary>
    public static Activity? StartClaudeCall(string route, string model, SessionRow session)
    {
        var activity = Source.StartActivity($"claude.{route}", ActivityKind.Client);
        activity?.SetTag("gen_ai.system", "anthropic");
        activity?.SetTag("gen_ai.request.model", model);
        activity?.SetTag("tutorai.session_id", session.Id);
        activity?.SetTag("tutorai.user_id", session.UserId);
        return activity;
    }
}
