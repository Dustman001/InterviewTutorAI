namespace TutorAI.Api;

/// <summary>Token counts for one Claude call (tutor reply or grading), filled in as the call reports them.</summary>
public sealed class ClaudeUsage
{
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
}
