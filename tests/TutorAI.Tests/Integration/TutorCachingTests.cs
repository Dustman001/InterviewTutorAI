using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using TutorAI.Api;
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
        var chat = api.Services.GetRequiredService<ITutorChat>();
        var session = new SessionRow
        {
            Id = Guid.NewGuid(),
            Mode = SessionMode.Conversation,
            // A topic unique to this run, so a cache warmed by an earlier run can't answer turn 1.
            ScenarioJson = JsonSerializer.Serialize(
                new Scenario(Topic: $"Travel (test run {Guid.NewGuid():N})", DurationMinutes: 10), JsonSerializerOptions.Web),
            StartedAt = DateTime.UtcNow,
        };
        var turns = new List<TurnRow>();
        var usages = new List<ClaudeUsage>();

        var spans = new List<Activity>();
        using var spanListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("tutorai.session_id"), session.Id))
                {
                    lock (spans) spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(spanListener);

        long chatTokens = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Telemetry.Name && instrument.Name == "tutorai.claude.tokens")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            // Other test classes may make Claude calls in parallel; they count too, so only a lower bound is checked.
            foreach (var tag in tags)
            {
                if (tag is { Key: "route", Value: Telemetry.ChatRoute })
                {
                    Interlocked.Add(ref chatTokens, value);
                }
            }
        });
        meterListener.Start();

        foreach (var learner in new[]
        {
            null,
            "Last summer I travelled to Portugal with my family. We stayed near the beach and went to the market every morning.",
            "I liked Porto the most, because the old town is beautiful and the food was really good. I would like to go back.",
        })
        {
            if (learner is not null)
            {
                turns.Add(new TurnRow { Role = TurnRole.User, Text = learner, StartedAt = DateTime.UtcNow });
            }

            var usage = new ClaudeUsage();
            var reply = new StringBuilder();
            await foreach (var fragment in chat.StreamReplyAsync(session, turns, usage))
            {
                reply.Append(fragment);
            }

            Assert.False(string.IsNullOrWhiteSpace(reply.ToString()));
            turns.Add(new TurnRow { Role = TurnRole.Tutor, Text = reply.ToString().Trim(), StartedAt = DateTime.UtcNow });
            usages.Add(usage);
        }

        var report = string.Join("; ", usages.Select((u, i) =>
            $"turn {i + 1}: input {u.InputTokens}, read {u.CacheReadTokens}, write {u.CacheWriteTokens}, output {u.OutputTokens}"));
        Assert.True(usages[0].CacheWriteTokens > 0, report);
        Assert.True(usages[1].CacheReadTokens > 0, report);
        Assert.True(usages[2].CacheReadTokens > usages[1].CacheReadTokens, report);
        // Uncached input per turn stays small: only the newest messages are billed at the full price.
        Assert.True(usages[2].InputTokens < usages[2].CacheReadTokens, report);

        Assert.Equal(3, spans.Count);
        Assert.All(spans, span =>
        {
            Assert.Equal("claude.chat", span.OperationName);
            Assert.Equal("claude-sonnet-5-5", span.GetTagItem("gen_ai.request.model"));
            Assert.True((long)span.GetTagItem("gen_ai.usage.output_tokens")! > 0);
        });
        var expected = usages.Sum(u => u.InputTokens + u.OutputTokens + u.CacheReadTokens + u.CacheWriteTokens);
        Assert.True(Interlocked.Read(ref chatTokens) >= expected, $"metric {chatTokens}, usage {expected}");
    }
}
