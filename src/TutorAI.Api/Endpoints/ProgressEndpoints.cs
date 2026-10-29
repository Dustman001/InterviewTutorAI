using System.Security.Claims;
using TutorAI.Api.Auth;
using TutorAI.Api.Data;

namespace TutorAI.Api.Endpoints;

/// <summary>One graded session on the progress page.</summary>
public sealed record ProgressPointDto(
    Guid SessionId,
    DateTime StartedAt,
    string Mode,
    string? Fluency,
    string? Grammar,
    string? Vocabulary,
    string? Interaction,
    string? Pronunciation,
    string Overall,
    double? WordsPerMinute,
    double? PauseRatio,
    double? PronunciationScore);

public sealed record ProgressDto(int UserId, string Username, IReadOnlyList<ProgressPointDto> Points);

public static class ProgressEndpoints
{
    /// <summary>The most recent graded sessions shown, oldest first.</summary>
    public const int MaxPoints = 200;

    public static void MapProgressEndpoints(this IEndpointRouteBuilder app)
    {
        // A user's graded sessions over time. Admins may pass any userId; everyone else only their own.
        app.MapGet("/api/progress", async (
            string? mode, int? userId, ClaimsPrincipal principal, SessionRepository sessions, UserRepository users,
            CancellationToken ct) =>
        {
            var self = principal.UserId();
            var target = userId ?? self;
            if (target != self && !principal.IsAdmin())
            {
                return Results.Json(new { error = "You can only see your own progress." }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (mode is not null && !SessionModes.IsValid(mode))
            {
                return Results.BadRequest(new { error = $"Mode must be '{SessionModes.Conversation}' or '{SessionModes.Interview}'." });
            }

            if (await users.GetByIdAsync(target, ct) is not { } user)
            {
                return Results.NotFound();
            }

            var rows = await sessions.GetProgressAsync(target, mode, MaxPoints, ct);
            return Results.Ok(new ProgressDto(user.Id, user.Username, [.. rows.Select(r => new ProgressPointDto(
                r.SessionId, DateTime.SpecifyKind(r.StartedAt, DateTimeKind.Utc), r.Mode,
                r.Fluency, r.Grammar, r.Vocabulary, r.Interaction, r.Pronunciation, r.Overall,
                r.WordsPerMinute, r.PauseRatio, r.PronunciationScore))]));
        });
    }
}
