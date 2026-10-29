using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TutorAI.Api.Conversation;
using TutorAI.Api.Data;

namespace TutorAI.Tests.Integration;

/// <summary>Real Claude calls (Sonnet, low effort, three short turns): the conversation prefix must come from the cache.</summary>
[Trait("Category", "Integration")]
public sealed class TutorCachingTests(ThrowawayDatabase database) : IClassFixture<ThrowawayDatabase>
{
    [E2EFact(claude: true)]
    public async Task Each_turn_reads_the_earlier_conversation_from_the_cache()
    {
        await using var api = TestApi.Create(database.ConnectionString);
        var chat = api.Services.GetRequiredService<TutorChat>();
        var session = new SessionRow
        {
            Id = Guid.NewGuid(),
            Mode = SessionModes.Conversation,
            // A topic unique to this run, so a cache warmed by an earlier run can't answer turn 1.
            ScenarioJson = JsonSerializer.Serialize(
                new Scenario(Topic: $"Travel (test run {Guid.NewGuid():N})", DurationMinutes: 10), JsonSerializerOptions.Web),
            StartedAt = DateTime.UtcNow,
        };
        var turns = new List<TurnRow>();
        var usages = new List<ClaudeUsage>();

        foreach (var learner in new[]
        {
            null,
            "Last summer I travelled to Portugal with my family. We stayed near the beach and went to the market every morning.",
            "I liked Porto the most, because the old town is beautiful and the food was really good. I would like to go back.",
        })
        {
            if (learner is not null)
            {
                turns.Add(new TurnRow { Role = TurnRoles.User, Text = learner, StartedAt = DateTime.UtcNow });
            }

            var usage = new ClaudeUsage();
            var reply = new StringBuilder();
            await foreach (var fragment in chat.StreamReplyAsync(session, turns, usage))
            {
                reply.Append(fragment);
            }

            Assert.False(string.IsNullOrWhiteSpace(reply.ToString()));
            turns.Add(new TurnRow { Role = TurnRoles.Tutor, Text = reply.ToString().Trim(), StartedAt = DateTime.UtcNow });
            usages.Add(usage);
        }

        var report = string.Join("; ", usages.Select((u, i) =>
            $"turn {i + 1}: input {u.InputTokens}, read {u.CacheReadTokens}, write {u.CacheWriteTokens}, output {u.OutputTokens}"));
        Assert.True(usages[0].CacheWriteTokens > 0, report);
        Assert.True(usages[1].CacheReadTokens > 0, report);
        Assert.True(usages[2].CacheReadTokens > usages[1].CacheReadTokens, report);
        // Uncached input per turn stays small: only the newest messages are billed at the full price.
        Assert.True(usages[2].InputTokens < usages[2].CacheReadTokens, report);
    }
}
