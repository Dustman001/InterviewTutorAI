using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using TutorAI.Api.Data;

namespace TutorAI.Api.Conversation;

/// <summary>Produces the tutor's next spoken turn with Claude, streamed as text fragments.</summary>
public sealed class TutorChat(AnthropicClient client, IOptions<AnthropicOptions> options)
{
    private const string RefusalFallback = "Sorry, I can't continue with that. Let's talk about something else. What would you like to discuss?";

    public async IAsyncEnumerable<string> StreamReplyAsync(
        SessionRow session, IReadOnlyList<TurnRow> turns, [EnumeratorCancellation] CancellationToken ct)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(session.ScenarioJson, JsonSerializerOptions.Web) ?? new Scenario();
        var parameters = new MessageCreateParams
        {
            Model = options.Value.ConversationModel,
            // Thinking tokens count against this budget, so leave room beyond the short spoken reply.
            MaxTokens = 4096,
            System = ScenarioPrompts.Build(session, scenario),
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages = BuildMessages(session, scenario, turns, DateTime.UtcNow),
        };

        var wroteText = false;
        var refused = false;
        await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, cancellationToken: ct))
        {
            if (streamEvent.TryPickContentBlockDelta(out var blockDelta) && blockDelta.Delta.TryPickText(out var text))
            {
                if (text.Text.Length > 0)
                {
                    wroteText = true;
                    yield return text.Text;
                }
            }
            else if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason?.ToString() == "refusal")
            {
                refused = true;
            }
        }

        if (refused && !wroteText)
        {
            yield return RefusalFallback;
        }
    }

    internal static List<MessageParam> BuildMessages(
        SessionRow session, Scenario scenario, IReadOnlyList<TurnRow> turns, DateTime utcNow)
    {
        List<MessageParam> messages = [new() { Role = Role.User, Content = ScenarioPrompts.Kickoff }];
        foreach (var turn in turns)
        {
            messages.Add(new()
            {
                Role = turn.Role == TurnRoles.User ? Role.User : Role.Assistant,
                Content = turn.Text,
            });
        }

        var planned = scenario.DurationMinutes > 0 ? scenario.DurationMinutes : 10;
        var timeIsUp = utcNow - DateTime.SpecifyKind(session.StartedAt, DateTimeKind.Utc) >= TimeSpan.FromMinutes(planned);
        if (timeIsUp && turns.Count > 0 && turns[^1].Role == TurnRoles.User)
        {
            messages[^1] = new() { Role = Role.User, Content = turns[^1].Text + ScenarioPrompts.TimeUpNote };
        }

        return messages;
    }
}
