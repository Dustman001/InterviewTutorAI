using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using TutorAI.Api.Data;

namespace TutorAI.Api.Assessment;

public sealed record DimensionGrade(string Level, string Confidence, List<string> Evidence, string Advice);

public sealed record Correction(string Original, string Corrected, string Explanation);

public sealed record GradingResult(
    DimensionGrade Fluency,
    DimensionGrade Grammar,
    DimensionGrade Vocabulary,
    DimensionGrade Interaction,
    string Summary,
    List<Correction> TopCorrections);

/// <summary>Grades a session's transcript. The real one calls Claude; tests swap in a fake.</summary>
public interface ICefrGrader
{
    /// <summary>The model id stored with the assessment.</summary>
    string Model { get; }

    /// <summary>Stored with the assessment, so a grade can be traced to the prompt that produced it.</summary>
    string PromptVersion { get; }

    /// <param name="usage">Filled in with the call's token counts.</param>
    Task<GradingResult> GradeAsync(
        SessionRow session, IReadOnlyList<TurnRow> turns, FluencyStats fluency, ClaudeUsage usage, CancellationToken ct);
}

/// <summary>Grades the learner's side of a transcript against the CEFR spoken-language descriptors using Claude.</summary>
public sealed class CefrGrader(
    AnthropicClient client, IOptions<AnthropicOptions> options, IOptions<AssessmentOptions> assessment) : ICefrGrader
{
    /// <summary>Change this whenever the system prompt, the schema or the request format changes.</summary>
    public const string CurrentPromptVersion = "2026-10-29";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string SystemPrompt =
        """
        You are an experienced CEFR oral examiner for English. You assess one learner's spoken performance in a
        recorded session with an AI conversation partner, and you report a CEFR level for four criteria.

        The transcript was produced by automatic speech recognition:
        - Punctuation, capitalisation and spelling come from the recogniser and are not evidence of anything.
        - The recogniser can mis-hear words and usually drops filled pauses. Do not treat an isolated odd word
          as a learner error when a mis-recognition is the likelier explanation.
        - Grade only the LEARNER lines. TUTOR lines are context for what the learner was responding to.

        Use these levels only: A1, A2, A2+, B1, B1+, B2, B2+, C1, C2. Give a plus level when the performance
        clearly exceeds a level without consistently meeting the next one. Grade what was demonstrated: a
        simple task can cap the evidence available, in which case give the level the evidence supports and
        lower your confidence instead of guessing upward.

        Criteria and descriptors (CEFR qualitative aspects of spoken language use):

        GRAMMAR (accuracy)
        - A1: only limited control of a few memorised simple structures.
        - A2: uses some simple structures correctly but still systematically makes basic mistakes.
        - B1: reasonably accurate with a repertoire of frequent routines and patterns in predictable situations.
        - B2: relatively high degree of control; no errors that cause misunderstanding; can correct most mistakes.
        - C1: consistently high accuracy; errors are rare, hard to spot and usually self-corrected.
        - C2: consistent control of complex language even while attention is elsewhere.

        VOCABULARY (range)
        - A1: very basic repertoire of words and simple phrases about personal details and concrete needs.
        - A2: basic sentence patterns and memorised phrases for limited information in everyday situations.
        - B1: enough language to get by on familiar topics, with hesitation and circumlocution.
        - B2: sufficient range for clear descriptions and viewpoints on general topics without much obvious
          searching for words; some complex sentence forms.
        - C1: broad range; chooses formulations to express themselves clearly and appropriately on general,
          academic, professional or leisure topics without restricting what they want to say.
        - C2: great flexibility reformulating ideas to convey finer shades of meaning; idiom and colloquialism.

        FLUENCY
        - A1: very short, isolated, mainly pre-packaged utterances with much pausing to search for expressions.
        - A2: makes themselves understood in very short utterances; pauses, false starts and reformulation are
          very evident.
        - B1: keeps going comprehensibly, though pausing for grammatical and lexical planning and repair is very
          evident, especially in longer stretches.
        - B2: stretches of language at a fairly even tempo; can be hesitant when searching for patterns, with
          few noticeably long pauses.
        - C1: fluent and spontaneous, almost effortless; only a conceptually difficult subject hinders the flow.
        - C2: spontaneous at length with a natural colloquial flow, backtracking around difficulty smoothly.
        You are given timing measurements taken from the audio (speech rate, pause ratio, long pauses, mean run
        length, an acoustic fluency score). Weigh them together with the transcript: false starts, repetition,
        abandoned sentences and length of turns. As a rough guide for conversational English, around 90 words
        per minute or less with frequent long pauses suggests A2-B1, 110-140 with moderate pausing suggests B2,
        and 140 or more with few long pauses is compatible with C1-C2, but rate alone never decides the level.
        When the measurements are missing, judge from the transcript alone and report low confidence.

        INTERACTION
        - A1: asks and answers questions about personal details; interaction depends on repetition and repair.
        - A2: answers questions and responds to simple statements; rarely keeps a conversation going unaided.
        - B1: initiates, maintains and closes simple conversation on familiar topics; can repeat back to confirm
          understanding.
        - B2: initiates discourse, takes turns appropriately, helps the discussion along, confirms comprehension
          and invites the other speaker in.
        - C1: selects suitable phrases to get or keep the floor and relates contributions skilfully to the other
          speaker's.
        - C2: interacts with ease, picking up cues and interweaving contributions naturally.
        In an interview, also consider whether answers address the question asked, are developed with relevant
        detail, and whether the learner handles follow-up probes and asks for clarification when needed.

        For each criterion give: the level; your confidence (low, medium or high); two to four short verbatim
        quotes from LEARNER lines as evidence; and one or two sentences of specific, practical advice aimed at
        reaching the next level. Then give a two or three sentence overall summary addressed to the learner as
        "you", and up to six of the most useful corrections: the learner's original words quoted verbatim, a
        natural corrected version, and a one-sentence explanation. Prefer recurring or high-impact errors over
        one-off slips. If there is nothing worth correcting, return an empty list.
        """;

    private static readonly Dictionary<string, JsonElement> Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
        """
        {
          "type": "object",
          "properties": {
            "fluency": { "$ref": "#/$defs/grade" },
            "grammar": { "$ref": "#/$defs/grade" },
            "vocabulary": { "$ref": "#/$defs/grade" },
            "interaction": { "$ref": "#/$defs/grade" },
            "summary": { "type": "string" },
            "topCorrections": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "original": { "type": "string" },
                  "corrected": { "type": "string" },
                  "explanation": { "type": "string" }
                },
                "required": ["original", "corrected", "explanation"],
                "additionalProperties": false
              }
            }
          },
          "required": ["fluency", "grammar", "vocabulary", "interaction", "summary", "topCorrections"],
          "additionalProperties": false,
          "$defs": {
            "grade": {
              "type": "object",
              "properties": {
                "level": { "type": "string", "enum": ["A1", "A2", "A2+", "B1", "B1+", "B2", "B2+", "C1", "C2"] },
                "confidence": { "type": "string", "enum": ["low", "medium", "high"] },
                "evidence": { "type": "array", "items": { "type": "string" } },
                "advice": { "type": "string" }
              },
              "required": ["level", "confidence", "evidence", "advice"],
              "additionalProperties": false
            }
          }
        }
        """)!;

    public string Model => options.Value.GradingModel;

    public string PromptVersion => CurrentPromptVersion;

    public async Task<GradingResult> GradeAsync(
        SessionRow session, IReadOnlyList<TurnRow> turns, FluencyStats fluency, ClaudeUsage usage, CancellationToken ct)
    {
        usage.Model = Model;
        using var activity = Telemetry.StartClaudeCall(Telemetry.GradeRoute, Model, session);
        var response = await client.Messages.Create(
            new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 16000,
                // Cached only on request: a single grading can't reuse the entry before it expires.
                System = assessment.Value.CacheGraderPrompt
                    ? (MessageCreateParamsSystem)new List<TextBlockParam>
                    {
                        new() { Text = SystemPrompt, CacheControl = new CacheControlEphemeral() },
                    }
                    : SystemPrompt,
                OutputConfig = new OutputConfig
                {
                    Effort = Effort.High,
                    Format = new JsonOutputFormat { Schema = Schema },
                },
                Messages = [new() { Role = Role.User, Content = BuildRequest(session, turns, fluency) }],
            },
            cancellationToken: ct);

        usage.InputTokens = response.Usage.InputTokens;
        usage.OutputTokens = response.Usage.OutputTokens;
        usage.CacheReadTokens = response.Usage.CacheReadInputTokens ?? 0;
        usage.CacheWriteTokens = response.Usage.CacheCreationInputTokens ?? 0;
        Telemetry.RecordUsage(activity, Telemetry.GradeRoute, usage);

        var stopReason = response.StopReason?.ToString();
        activity?.SetTag("gen_ai.response.finish_reasons", stopReason);
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Grading returned no content (stop reason: {stopReason}).");
        }

        try
        {
            return JsonSerializer.Deserialize<GradingResult>(text, Json)
                ?? throw new JsonException("Grading result was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Grading returned unreadable output (stop reason: {stopReason}).", ex);
        }
    }

    internal static string BuildRequest(SessionRow session, IReadOnlyList<TurnRow> turns, FluencyStats fluency)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Session type: {session.Mode}");
        sb.AppendLine($"Scenario: {session.ScenarioJson}");
        sb.AppendLine();
        sb.AppendLine("Timing measurements for the learner's speech (null means not measured):");
        sb.AppendLine(JsonSerializer.Serialize(fluency, Json));
        sb.AppendLine();
        sb.AppendLine("Transcript:");
        foreach (var turn in turns)
        {
            sb.Append(turn.Role == TurnRole.User ? "LEARNER: " : "TUTOR: ").AppendLine(turn.Text.Trim());
        }

        return sb.ToString();
    }
}
