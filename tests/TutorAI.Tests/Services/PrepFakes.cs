using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TutorAI.Api;
using TutorAI.Api.Data;
using TutorAI.Api.Prep;

namespace TutorAI.Tests.Services;

/// <summary>Prep runs, actions and drills in memory, with the same uniqueness rules as the SQL tables.</summary>
internal sealed class InMemoryPrepRepository : IPrepRepository
{
    public Dictionary<Guid, PrepRunRow> Runs { get; } = [];
    public List<PrepActionRow> Actions { get; } = [];
    public List<DrillRow> Drills { get; } = [];
    public PrepRunOutcome? LastOutcome { get; private set; }

    public Task<(PrepRunRow Run, bool Created)> CreateRunAsync(PrepRunRow run, CancellationToken ct)
    {
        if (run.IdempotencyKey is not null && Runs.Values.FirstOrDefault(r => r.UserId == run.UserId && r.IdempotencyKey == run.IdempotencyKey) is { } existing)
        {
            return Task.FromResult((existing, false));
        }

        Runs[run.Id] = run;
        return Task.FromResult((run, true));
    }

    public Task<PrepRunRow?> FindRunByKeyAsync(int userId, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(Runs.Values.FirstOrDefault(r => r.UserId == userId && r.IdempotencyKey == idempotencyKey));

    public Task<PrepRunRow?> GetRunAsync(Guid id, int userId, CancellationToken ct) =>
        Task.FromResult(Runs.GetValueOrDefault(id) is { } run && run.UserId == userId ? run : null);

    public Task<IReadOnlyList<PrepRunRow>> ListRunsAsync(int userId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PrepRunRow>>([.. Runs.Values.Where(r => r.UserId == userId).OrderByDescending(r => r.CreatedAt).Take(limit)]);

    public Task<int> CountRunsSinceAsync(int userId, DateTime since, CancellationToken ct) =>
        Task.FromResult(Runs.Values.Count(r => r.UserId == userId && r.CreatedAt >= since));

    public Task SetQuestionsAsync(Guid runId, string questionsJson, CancellationToken ct)
    {
        Runs[runId].QuestionsJson = questionsJson;
        return Task.CompletedTask;
    }

    public Task FinishRunAsync(Guid runId, PrepRunOutcome outcome, CancellationToken ct)
    {
        LastOutcome = outcome;
        var run = Runs[runId];
        run.Status = outcome.Status;
        run.StopReason = outcome.StopReason;
        run.Summary = outcome.Summary;
        run.Steps = outcome.Steps;
        run.InputTokens = (int)outcome.InputTokens;
        run.OutputTokens = (int)outcome.OutputTokens;
        run.CompletedAt = outcome.CompletedAt;
        return Task.CompletedTask;
    }

    public Task<PrepActionRow?> GetActionByKeyAsync(Guid runId, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(Actions.FirstOrDefault(a => a.RunId == runId && a.IdempotencyKey == idempotencyKey));

    public Task<PrepActionRow?> GetActionAsync(long id, Guid runId, int userId, CancellationToken ct) =>
        Task.FromResult(Actions.FirstOrDefault(a => a.Id == id && a.RunId == runId && Runs.GetValueOrDefault(runId)?.UserId == userId));

    public Task<IReadOnlyList<PrepActionRow>> ListActionsAsync(Guid runId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PrepActionRow>>([.. Actions.Where(a => a.RunId == runId)]);

    public Task<PrepActionRow> AddActionAsync(PrepActionRow action, IUnitOfWork work, CancellationToken ct)
    {
        action.Id = Actions.Count + 1;
        ((FakeUnitOfWork)work).OnCommit(() =>
        {
            if (Actions.Any(a => a.RunId == action.RunId && a.IdempotencyKey == action.IdempotencyKey))
            {
                throw new InvalidOperationException("Duplicate (RunId, IdempotencyKey).");
            }

            Actions.Add(action);
        });
        return Task.FromResult(action);
    }

    public Task<bool> DecideActionAsync(long id, PrepActionStatus status, string? resultJson, DateTime now, IUnitOfWork work, CancellationToken ct)
    {
        var action = Actions.FirstOrDefault(a => a.Id == id);
        if (action is not { Status: var current } || current != PrepActionStatus.PendingConfirmation)
        {
            return Task.FromResult(false);
        }

        ((FakeUnitOfWork)work).OnCommit(() =>
        {
            action.Status = status;
            action.ResultJson = resultJson ?? action.ResultJson;
            action.DecidedAt = now;
        });
        return Task.FromResult(true);
    }

    public Task<DrillRow> AddDrillAsync(DrillRow drill, IUnitOfWork work, CancellationToken ct)
    {
        ((FakeUnitOfWork)work).OnCommit(() => Drills.Add(drill));
        return Task.FromResult(drill);
    }

    public Task<DrillRow?> GetDrillAsync(Guid id, int userId, CancellationToken ct) =>
        Task.FromResult(Drills.FirstOrDefault(d => d.Id == id && d.UserId == userId));

    public Task<IReadOnlyList<DrillRow>> ListDrillsAsync(int userId, DateTime now, int recentDays, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DrillRow>>([.. Drills.Where(d => d.UserId == userId)]);

    public Task<IReadOnlyList<DrillRow>> ListDrillsForRunAsync(Guid runId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DrillRow>>([.. Drills.Where(d => d.RunId == runId)]);

    public Task<bool> UpdateDrillAsync(Guid id, int userId, DrillStatus status, Guid? sessionId, DateTime now, CancellationToken ct)
    {
        if (Drills.FirstOrDefault(d => d.Id == id && d.UserId == userId && d.Status == DrillStatus.Scheduled) is not { } drill)
        {
            return Task.FromResult(false);
        }

        drill.Status = status;
        drill.SessionId = sessionId ?? drill.SessionId;
        drill.UpdatedAt = now;
        return Task.FromResult(true);
    }
}

/// <summary>
/// Stands in for Claude: answers each model call with the next scripted reply (tool calls or text), reports fixed
/// token usage, and records what it was sent.
/// </summary>
internal sealed class ScriptedChatClient(params ChatMessage[] replies) : IChatClient
{
    private int _next;

    public long InputTokensPerCall { get; init; } = 1_000;
    public long OutputTokensPerCall { get; init; } = 200;

    /// <summary>The messages of each call, in order.</summary>
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add([.. messages]);
        var reply = _next < replies.Length ? replies[_next++] : new ChatMessage(ChatRole.Assistant, "Done.");
        return Task.FromResult(new ChatResponse(reply)
        {
            Usage = new UsageDetails { InputTokenCount = InputTokensPerCall, OutputTokenCount = OutputTokensPerCall },
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>A model turn that calls one tool.</summary>
    public static ChatMessage Call(string tool, object? args = null) =>
        new(ChatRole.Assistant, [new FunctionCallContent(
            Guid.NewGuid().ToString("N"), tool,
            args is null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args, JsonSerializerOptions.Web)))]);

    public static ChatMessage Text(string text) => new(ChatRole.Assistant, text);
}

internal sealed class FixedChatClientFactory(IChatClient client) : IPrepChatClientFactory
{
    public IChatClient Create(string model) => client;
}

/// <summary>The prep agent and service over in-memory stores, for unit tests and evals.</summary>
internal sealed class PrepHarness
{
    public const int UserId = 7;

    public PrepHarness(IPrepChatClientFactory chat, PrepOptions? options = null, DateTimeOffset? now = null)
    {
        Clock = new FixedClock(now ?? new DateTimeOffset(2026, 11, 2, 15, 30, 0, TimeSpan.Zero));
        Options = options ?? new PrepOptions();
        Agent = new PrepAgent(
            chat, Prep, Practice, Sessions, Database, Clock, Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Options.Options.Create(Limits), NullLoggerFactory.Instance);
        Service = new PrepService(
            Agent, Prep, Practice, Sessions, Database, Clock, Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Options.Options.Create(Limits));
    }

    public FixedClock Clock { get; }
    public PrepOptions Options { get; }
    public SessionLimitsOptions Limits { get; } = new();
    public InMemoryPrepRepository Prep { get; } = new();
    public InMemoryPracticeRepository Practice { get; } = new();
    public InMemorySessionRepository Sessions { get; } = new();
    public FakeUnitOfWorkFactory Database { get; } = new();
    public PrepAgent Agent { get; }
    public PrepService Service { get; }

    public PrepRunRow NewRun(string jobDescription = "Backend engineer (.NET, SQL Server, Azure). Lead design reviews and mentor juniors.",
        string? role = "Backend engineer", string? seniority = "Senior")
    {
        var run = new PrepRunRow
        {
            Id = Guid.NewGuid(), UserId = UserId, JobDescription = jobDescription, Role = role, Seniority = seniority,
            Status = PrepRunStatus.Running, Model = Options.Model, CreatedAt = Clock.GetUtcNow().UtcDateTime,
        };
        Prep.Runs[run.Id] = run;
        return run;
    }

    /// <summary>A graded session for the user, with the grader's advice and corrections.</summary>
    public void AddGradedSession(DateTime startedAt, string overall, string interaction, string grammar, string advice, params (string Original, string Corrected)[] corrections)
    {
        var session = Sessions.Add(new SessionRow
        {
            Id = Guid.NewGuid(), UserId = UserId, Mode = SessionMode.Interview, StartedAt = startedAt, Status = SessionStatus.Ended,
        });
        var grade = new TutorAI.Api.Assessment.DimensionGrade(overall, "medium", [], advice);
        var detail = new TutorAI.Api.Assessment.GradingResult(
            grade, grade with { Level = grammar }, grade, grade with { Level = interaction }, "Summary.",
            [.. corrections.Select(c => new TutorAI.Api.Assessment.Correction(c.Original, c.Corrected, "Explanation."))]);
        Sessions.Assessments[session.Id] = new AssessmentRow
        {
            SessionId = session.Id, Overall = overall, Fluency = overall, Grammar = grammar, Vocabulary = overall, Interaction = interaction,
            DetailJson = JsonSerializer.Serialize(detail, JsonSerializerOptions.Web),
        };
    }
}
