using System.Text.Json;
using Microsoft.Extensions.Options;
using TutorAI.Api.Data;

namespace TutorAI.Api.Assessment;

public sealed record SessionMetrics(FluencyStats Fluency, PronunciationStats Pronunciation);

public sealed record AssessmentLevels(
    string? Fluency, string? Grammar, string? Vocabulary, string? Interaction, string? Pronunciation, string? Overall);

/// <param name="Sufficient">False when the learner did not speak enough to grade; levels are then null.</param>
public sealed record AssessmentResponse(
    Guid SessionId,
    bool Sufficient,
    string? Reason,
    AssessmentLevels Levels,
    SessionMetrics Metrics,
    GradingResult? Detail,
    string? Model,
    DateTime CreatedAt);

public sealed class AssessmentService(
    SessionRepository repository, CefrGrader grader, IOptions<AssessmentOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Ends the session and returns its assessment, grading it on first call.</summary>
    public async Task<AssessmentResponse?> EndAndAssessAsync(Guid sessionId, bool regrade, CancellationToken ct)
    {
        var session = await repository.GetSessionAsync(sessionId, ct);
        if (session is null)
        {
            return null;
        }

        await repository.EndSessionAsync(sessionId, ct);

        if (!regrade && await repository.GetAssessmentAsync(sessionId, ct) is { } existing)
        {
            return ToResponse(existing);
        }

        var turns = await repository.GetTurnsAsync(sessionId, includePronunciationJson: true, ct);
        var metrics = ComputeMetrics(turns);
        var row = new AssessmentRow { SessionId = sessionId, MetricsJson = JsonSerializer.Serialize(metrics, Json) };

        var minWords = options.Value.MinUserWords;
        if (metrics.Fluency.TotalWords < minWords)
        {
            row.DetailJson = JsonSerializer.Serialize(
                new InsufficientDetail($"You spoke {metrics.Fluency.TotalWords} words; at least {minWords} are needed for a reliable estimate."),
                Json);
        }
        else
        {
            var grading = await grader.GradeAsync(session, turns, metrics.Fluency, ct);
            row.Fluency = grading.Fluency.Level;
            row.Grammar = grading.Grammar.Level;
            row.Vocabulary = grading.Vocabulary.Level;
            row.Interaction = grading.Interaction.Level;
            row.Pronunciation = metrics.Pronunciation.Composite is { } composite
                ? CefrMapper.FromPronunciationScore(composite)
                : null;
            row.Overall = CefrMapper.Median([row.Fluency, row.Grammar, row.Vocabulary, row.Interaction, row.Pronunciation]);
            row.DetailJson = JsonSerializer.Serialize(grading, Json);
            row.Model = grader.Model;
        }

        return ToResponse(await repository.SaveAssessmentAsync(row, ct));
    }

    public async Task<AssessmentResponse?> GetAsync(Guid sessionId, CancellationToken ct) =>
        await repository.GetAssessmentAsync(sessionId, ct) is { } row ? ToResponse(row) : null;

    public static SessionMetrics ComputeMetrics(IReadOnlyList<TurnRow> turns)
    {
        var userTurns = turns
            .Where(t => t.Role == TurnRoles.User)
            .Select(t => (t.WordCount, Phrases: AzurePronunciation.ParseTurn(t.PronunciationJson)))
            .ToList();

        return new SessionMetrics(
            FluencyMetrics.Compute(userTurns),
            PronunciationAggregator.Compute(userTurns.SelectMany(t => t.Phrases)));
    }

    private static AssessmentResponse ToResponse(AssessmentRow row)
    {
        var sufficient = row.Overall is not null;
        return new AssessmentResponse(
            row.SessionId,
            sufficient,
            sufficient ? null : JsonSerializer.Deserialize<InsufficientDetail>(row.DetailJson, Json)?.Reason,
            new AssessmentLevels(row.Fluency, row.Grammar, row.Vocabulary, row.Interaction, row.Pronunciation, row.Overall),
            JsonSerializer.Deserialize<SessionMetrics>(row.MetricsJson, Json)!,
            sufficient ? JsonSerializer.Deserialize<GradingResult>(row.DetailJson, Json) : null,
            row.Model,
            DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));
    }

    private sealed record InsufficientDetail(string Reason);
}
