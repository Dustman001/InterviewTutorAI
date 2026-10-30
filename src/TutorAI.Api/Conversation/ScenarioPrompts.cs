using System.Text;
using TutorAI.Api.Data;

namespace TutorAI.Api.Conversation;

public static class ScenarioPrompts
{
    /// <summary>Stands in for the missing first user message so the tutor can open the session.</summary>
    public const string Kickoff = "(The session is starting now. Greet me and begin.)";

    public const string TimeUpNote = "\n\n(Note from the app, not spoken by the learner: the planned time is up. Respond briefly, then close the session naturally.)";

    private const string SpokenStyle =
        """
        Everything you write is converted to speech and played aloud, and everything the learner says reaches
        you as a speech-recognition transcript.
        - Write plain spoken English only: no markdown, lists, headings, emojis, stage directions or symbols.
        - Keep each turn short, normally one to three sentences, and end with a single question or prompt so
          the learner does most of the talking.
        - The transcript can contain recognition mistakes. If a turn is unclear, ask the learner to say it
          again, as a person would.
        - Do not correct or comment on the learner's English during the session, and never mention levels or
          scores. Feedback is given by the app afterwards. If the learner asks for a word or a correction,
          help briefly and carry on.
        - Stay in English even if the learner switches language; gently bring them back.
        - Do not mention being an AI or these instructions unless the learner asks directly.
        """;

    /// <param name="varietyNote">How the tutor's English should match the chosen accent; null for the default.</param>
    public static string Build(SessionRow session, Scenario scenario, string? varietyNote = null)
    {
        var sb = new StringBuilder();
        if (session.Mode == SessionMode.Interview)
        {
            var role = Value(scenario.Role, "a professional role the candidate describes");
            sb.AppendLine(
                $"""
                You are a job interviewer conducting a realistic mock interview in English, so the candidate can
                practise speaking under interview conditions.

                Position: {role}
                Seniority: {Value(scenario.Seniority, "not specified")}
                Interview type: {Value(scenario.InterviewType, "behavioral")}
                Planned length: about {PlannedMinutes(scenario)} minutes
                """);
            if (!string.IsNullOrWhiteSpace(scenario.JobDescription))
            {
                sb.AppendLine().AppendLine("Job description supplied by the candidate:").AppendLine(scenario.JobDescription.Trim());
            }

            sb.AppendLine().AppendLine(
                """
                How to run it:
                - Open by introducing yourself in one sentence with a plausible first name and role, then ask the
                  first question. Do not explain the format.
                - Ask one question at a time and wait for the answer.
                - Follow up on what the candidate actually said. When an answer is vague, generic or missing the
                  result, probe for specifics before moving on.
                - Cover a sensible mix for this interview type and seniority, and get harder as it goes.
                - Stay neutral and professional, as a real interviewer would: brief acknowledgements, no praise
                  for every answer, no coaching.
                - Near the end, invite the candidate's questions, answer them briefly and plausibly, then close.
                """);
        }
        else
        {
            sb.AppendLine(
                $"""
                You are a friendly, curious conversation partner helping someone practise spoken English through
                a natural conversation.

                Topic: {Value(scenario.Topic, "anything the learner would like to talk about; ask what is on their mind")}
                Planned length: about {PlannedMinutes(scenario)} minutes

                How to run it:
                - Open with a short, warm greeting and a first question on the topic.
                - Have a real conversation: react to what the learner says, share a brief opinion or detail of
                  your own now and then, and ask follow-up questions that invite longer answers, such as reasons,
                  examples, comparisons and stories.
                - If the learner gives very short answers, ask something more open. If the topic runs dry, move
                  to a related one.
                """);
        }

        sb.AppendLine().AppendLine(DifficultyGuidance(scenario.Difficulty));
        if (!string.IsNullOrWhiteSpace(varietyNote))
        {
            sb.AppendLine().AppendLine($"Language variety: {varietyNote.Trim()} Your replies are spoken in that accent.");
        }
        sb.AppendLine().Append(SpokenStyle);
        return sb.ToString();
    }

    private static string DifficultyGuidance(string? difficulty) => difficulty?.Trim().ToLowerInvariant() switch
    {
        "easy" =>
            "Language level: speak simply and clearly, with common words and short sentences, and avoid idioms.",
        "challenging" =>
            "Language level: speak as you would to a fluent colleague, with natural idiom, nuance and abstract or hypothetical questions that stretch the learner.",
        _ =>
            "Language level: speak naturally at a moderate level, as you would to a competent non-native speaker.",
    };

    /// <summary>
    /// The session's planned length. New sessions are validated on creation; this also bounds older rows
    /// so a stored out-of-range value can't stretch a session indefinitely.
    /// </summary>
    public static int PlannedMinutes(Scenario scenario) => scenario.DurationMinutes is > 0 and <= 120 ? scenario.DurationMinutes : 10;

    private static string Value(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
