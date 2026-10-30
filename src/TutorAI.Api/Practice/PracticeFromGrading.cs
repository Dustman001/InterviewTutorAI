using System.Text.Json;
using TutorAI.Api.Assessment;
using TutorAI.Api.Data;

namespace TutorAI.Api.Practice;

/// <summary>
/// Turns a graded session's corrections and weak words into practice items for its owner, saved with the assessment.
/// Sessions without an owner (from before sign-in existed) get none.
/// </summary>
public sealed class PracticeFromGrading(IPracticeRepository repository, TimeProvider clock) : IGradingFollowUp
{
    public Task OnGradedAsync(GradedSession graded, IUnitOfWork work, CancellationToken ct)
    {
        if (graded.Session.UserId is not { } userId)
        {
            return Task.CompletedTask;
        }

        var accent = JsonSerializer.Deserialize<Scenario>(graded.Session.ScenarioJson, JsonSerializerOptions.Web)?.Accent;
        var batch = new PracticeBatch(
            userId, graded.Session.Id, accent, clock.GetUtcNow().UtcDateTime,
            PracticeSeeds.FromAssessment(graded.Grading, graded.Metrics.Pronunciation));
        return repository.UpsertAsync(batch, work, ct);
    }
}
