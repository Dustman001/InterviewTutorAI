using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace TutorAI.Api.Prep;

/// <summary>Thrown before a model call that would go past the run's step or token budget.</summary>
public sealed class AgentBudgetExceededException(string reason) : Exception(reason);

/// <summary>
/// Counts one run's model calls and tokens, and refuses the next call once either limit is reached. It sits
/// under the function-invoking client, so every tool round trip is one step. Tokens are checked before each
/// call: the call that crosses the limit completes (its output is bounded by MaxOutputTokens), the next one
/// is refused. One instance per run.
/// </summary>
public sealed class AgentBudget(IChatClient inner, int maxSteps, long maxTokens) : DelegatingChatClient(inner)
{
    private int _steps;
    private long _inputTokens;
    private long _outputTokens;

    public int Steps => Volatile.Read(ref _steps);
    public long InputTokens => Interlocked.Read(ref _inputTokens);
    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Reserve();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        Record(response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Reserve();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (var usage in update.Contents.OfType<UsageContent>())
            {
                Record(usage.Details);
            }

            yield return update;
        }
    }

    private void Reserve()
    {
        if (Steps >= maxSteps)
        {
            throw new AgentBudgetExceededException($"Stopped at the limit of {maxSteps} model calls per run.");
        }

        if (InputTokens + OutputTokens >= maxTokens)
        {
            throw new AgentBudgetExceededException($"Stopped at the limit of {maxTokens} tokens per run.");
        }

        Interlocked.Increment(ref _steps);
    }

    private void Record(UsageDetails? usage)
    {
        if (usage is null)
        {
            return;
        }

        Interlocked.Add(ref _inputTokens, usage.InputTokenCount ?? 0);
        Interlocked.Add(ref _outputTokens, usage.OutputTokenCount ?? 0);
    }
}
