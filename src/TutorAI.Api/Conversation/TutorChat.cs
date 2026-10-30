using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using TutorAI.Api.Data;
using TutorAI.Api.Speech;

namespace TutorAI.Api.Conversation;

/// <summary>Produces the tutor's next spoken turn, streamed as text fragments.</summary>
public interface ITutorChat
{
    /// <param name="turns">The session's stored turns, the learner's latest last.</param>
    /// <param name="usage">Filled in with the call's model and token counts as the stream reports them.</param>
    IAsyncEnumerable<string> StreamReplyAsync(
        SessionRow session, IReadOnlyList<TurnRow> turns, ClaudeUsage? usage = null, CancellationToken ct = default);
}

/// <summary>The tutor, played by Claude.</summary>
public sealed class TutorChat(
    AnthropicClient client, IOptions<AnthropicOptions> options, IOptions<AzureSpeechOptions> speech, ILogger<TutorChat> logger)
    : ITutorChat
{
    private const string RefusalFallback = "Sorry, I can't continue with that. Let's talk about something else. What would you like to discuss?";

    public async IAsyncEnumerable<string> StreamReplyAsync(
        SessionRow session, IReadOnlyList<TurnRow> turns, ClaudeUsage? usage = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        usage ??= new ClaudeUsage();
        usage.Model = options.Value.ConversationModel;
        using var activity = Telemetry.StartClaudeCall(Telemetry.ChatRoute, usage.Model, session);
        var timer = Stopwatch.StartNew();
        var scenario = JsonSerializer.Deserialize<Scenario>(session.ScenarioJson, JsonSerializerOptions.Web) ?? new Scenario();
        var parameters = new MessageCreateParams
        {
            Model = options.Value.ConversationModel,
            // Thinking tokens count against this budget, so leave room beyond the short spoken reply.
            MaxTokens = 4096,
            // The scenario prompt is fixed for the whole session: a cache read point every turn can reuse.
            System = new List<TextBlockParam>
            {
                new()
                {
                    Text = ScenarioPrompts.Build(session, scenario, AccentCatalog.Find(speech.Value, scenario.Accent)?.PromptNote),
                    CacheControl = new CacheControlEphemeral(),
                },
            },
            // Automatic caching moves a second breakpoint to the end of the growing conversation, so each
            // turn reads everything before it from the cache. Turns are seconds apart: the 5-minute TTL fits.
            CacheControl = new CacheControlEphemeral(),
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages = BuildMessages(session, scenario, turns),
        };

        var wroteText = false;
        var refused = false;
        await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, cancellationToken: ct))
        {
            if (streamEvent.TryPickContentBlockDelta(out var blockDelta) && blockDelta.Delta.TryPickText(out var text))
            {
                if (text.Text.Length > 0)
                {
                    if (!wroteText)
                    {
                        Telemetry.TurnFirstTokenMs.Record(timer.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("model", usage.Model));
                    }

                    wroteText = true;
                    yield return text.Text;
                }
            }
            else if (streamEvent.TryPickStart(out var start))
            {
                usage.InputTokens = start.Message.Usage.InputTokens;
                usage.CacheReadTokens = start.Message.Usage.CacheReadInputTokens ?? 0;
                usage.CacheWriteTokens = start.Message.Usage.CacheCreationInputTokens ?? 0;
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                usage.OutputTokens = messageDelta.Usage.OutputTokens;
                activity?.SetTag("gen_ai.response.finish_reasons", messageDelta.Delta.StopReason?.ToString());
                if (messageDelta.Delta.StopReason?.ToString() == "refusal")
                {
                    refused = true;
                }
            }
        }

        Telemetry.RecordUsage(activity, Telemetry.ChatRoute, usage);
        logger.LogInformation(
            "Tutor turn for session {SessionId}: {InputTokens} input, {CacheReadTokens} cache read, {CacheWriteTokens} cache write, {OutputTokens} output tokens",
            session.Id, usage.InputTokens, usage.CacheReadTokens, usage.CacheWriteTokens, usage.OutputTokens);

        if (refused && !wroteText)
        {
            yield return RefusalFallback;
        }
    }

    /// <remarks>
    /// The time-up note is decided per turn from the turn's own stored start time, never from the current
    /// time. A turn therefore renders identically on every later request, which keeps the cached prefix valid.
    /// </remarks>
    internal static List<MessageParam> BuildMessages(SessionRow session, Scenario scenario, IReadOnlyList<TurnRow> turns)
    {
        var plannedEnd = DateTime.SpecifyKind(session.StartedAt, DateTimeKind.Utc)
            .AddMinutes(ScenarioPrompts.PlannedMinutes(scenario));

        List<MessageParam> messages = [new() { Role = Role.User, Content = ScenarioPrompts.Kickoff }];
        foreach (var turn in turns)
        {
            var isUser = turn.Role == TurnRole.User;
            var lateUserTurn = isUser && DateTime.SpecifyKind(turn.StartedAt, DateTimeKind.Utc) >= plannedEnd;
            messages.Add(new()
            {
                Role = isUser ? Role.User : Role.Assistant,
                Content = lateUserTurn ? turn.Text + ScenarioPrompts.TimeUpNote : turn.Text,
            });
        }

        return messages;
    }

}
