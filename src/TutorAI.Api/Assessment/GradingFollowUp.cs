using TutorAI.Api.Data;

namespace TutorAI.Api.Assessment;

/// <summary>A session Claude has just graded.</summary>
public sealed record GradedSession(SessionRow Session, GradingResult Grading, SessionMetrics Metrics);

/// <summary>
/// Builds on a session's grading, e.g. practice drills from its corrections. Runs inside the transaction that saves
/// the assessment, so its writes commit or roll back with it. Not called for sessions too short to grade.
/// </summary>
public interface IGradingFollowUp
{
    Task OnGradedAsync(GradedSession graded, IUnitOfWork work, CancellationToken ct);
}
