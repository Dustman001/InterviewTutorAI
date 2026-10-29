using System.Security.Claims;
using System.Text.Json;
using TutorAI.Api.Auth;
using TutorAI.Api.Practice;

namespace TutorAI.Api.Endpoints;

/// <param name="DueAt">Null once mastered.</param>
public sealed record PracticeItemDto(
    long Id, string Kind, string Target, string? Original, string? Explanation, string? Locale, int Box,
    DateTime? DueAt, int Attempts, double? LastScore, double? BestScore, Guid? SessionId);

/// <param name="Items">Due items, longest overdue first, up to the requested limit.</param>
/// <param name="ActiveCount">Items not yet mastered, due or not.</param>
/// <param name="NextDueAt">When the next item not yet due falls due; null when none is scheduled.</param>
public sealed record PracticeDueDto(
    IReadOnlyList<PracticeItemDto> Items, int DueCount, int ActiveCount, int MasteredCount, DateTime? NextDueAt);

/// <param name="Phrases">Azure's detailed results for the attempt, from scripted assessment with the item's target as reference text.</param>
public sealed record PracticeAttemptRequest(JsonElement[]? Phrases);

public sealed record PracticeAttemptDto(double Score, bool Passed, IReadOnlyList<ScriptedWord> Words, PracticeItemDto Item);

public static class PracticeEndpoints
{
    public const int DefaultDueLimit = 20;
    public const int MaxDueLimit = 50;

    public static void MapPracticeEndpoints(this IEndpointRouteBuilder app)
    {
        var practice = app.MapGroup("/api/practice");

        practice.MapGet("/due", async (
            ClaimsPrincipal user, PracticeRepository repository, TimeProvider clock, int? limit, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var take = Math.Clamp(limit ?? DefaultDueLimit, 0, MaxDueLimit);
            var items = take == 0 ? [] : await repository.ListDueAsync(user.UserId(), now, take, ct);
            var counts = await repository.CountAsync(user.UserId(), now, ct);
            return Results.Ok(new PracticeDueDto(
                [.. items.Select(ToDto)], counts.Due, counts.Active, counts.Mastered, Utc(counts.NextDueAt)));
        });

        // The score is computed here from Azure's raw result; a pass moves the item up a box.
        practice.MapPost("/{id:long}/attempts", async (
            long id, PracticeAttemptRequest request, ClaimsPrincipal user, PracticeRepository repository,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (await repository.GetAsync(id, user.UserId(), ct) is not { } item)
            {
                return Results.NotFound();
            }

            if (ScriptedAssessment.Score(item.Target, request.Phrases, out var problem) is not { } result)
            {
                return Results.BadRequest(new { error = problem });
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var (box, dueAt) = PracticeSchedule.Next(item.Box, result.Score, now);
            var updated = await repository.RecordAttemptAsync(id, user.UserId(), result.Score, box, dueAt, now, ct);
            return updated is null
                ? Results.NotFound()
                : Results.Ok(new PracticeAttemptDto(
                    result.Score, result.Score >= PracticeSchedule.PassScore, result.Words, ToDto(updated)));
        }).RequireRateLimiting(SessionEndpoints.TurnsPolicy);

        practice.MapDelete("/{id:long}", async (long id, ClaimsPrincipal user, PracticeRepository repository, CancellationToken ct) =>
            await repository.DeleteAsync(id, user.UserId(), ct) ? Results.NoContent() : Results.NotFound());
    }

    private static PracticeItemDto ToDto(PracticeItemRow row) =>
        new(row.Id, row.Kind, row.Target, row.Original, row.Explanation, row.Locale, row.Box, Utc(row.DueAt),
            row.Attempts, row.LastScore, row.BestScore, row.SessionId);

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}
