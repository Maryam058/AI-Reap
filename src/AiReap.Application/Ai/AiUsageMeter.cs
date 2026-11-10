using System.Collections.Concurrent;

namespace AiReap.Application.Ai;

// Token usage the AI provider reported for each response, counted in memory since the API started.
// Migration-free: nothing is written to the database, so totals start again at zero after a restart and
// are per API instance. Every response the provider billed is counted - also one that was then rejected
// (cut off at the output limit, refused, or failing validation and repaired) - and every retry attempt
// separately, because each is billed separately. Provider-side totals (the Claude Console) stay the
// authority for billing.
public interface IAiUsageMeter
{
    void Record(string provider, string model, AiUsage usage);

    AiUsageSnapshot Snapshot();
}

public record AiUsage(long InputTokens, long OutputTokens, long CacheCreationInputTokens = 0, long CacheReadInputTokens = 0);

public record AiUsageTotals(long Requests, long InputTokens, long OutputTokens, long CacheCreationInputTokens, long CacheReadInputTokens);

public record AiUsageByModel(string Provider, string Model, AiUsageTotals Totals);

public record AiUsageCall(DateTime At, string Provider, string Model, AiUsage Usage);

public record AiUsageSnapshot(DateTime Since, AiUsageTotals Totals, IReadOnlyList<AiUsageByModel> ByModel, IReadOnlyList<AiUsageCall> Recent);

public sealed class AiUsageMeter : IAiUsageMeter
{
    private const int RecentCalls = 50;

    private readonly DateTime _since = DateTime.UtcNow;
    private readonly ConcurrentDictionary<(string Provider, string Model), Counter> _byModel = new();
    private readonly ConcurrentQueue<AiUsageCall> _recent = new();

    private sealed class Counter
    {
        public long Requests, Input, Output, CacheCreation, CacheRead;
    }

    public void Record(string provider, string model, AiUsage usage)
    {
        var counter = _byModel.GetOrAdd((provider, model), _ => new Counter());
        Interlocked.Increment(ref counter.Requests);
        Interlocked.Add(ref counter.Input, Math.Max(0, usage.InputTokens));
        Interlocked.Add(ref counter.Output, Math.Max(0, usage.OutputTokens));
        Interlocked.Add(ref counter.CacheCreation, Math.Max(0, usage.CacheCreationInputTokens));
        Interlocked.Add(ref counter.CacheRead, Math.Max(0, usage.CacheReadInputTokens));

        _recent.Enqueue(new AiUsageCall(DateTime.UtcNow, provider, model, usage));
        while (_recent.Count > RecentCalls && _recent.TryDequeue(out _))
        {
        }
    }

    public AiUsageSnapshot Snapshot()
    {
        var byModel = _byModel
            .Select(p => new AiUsageByModel(p.Key.Provider, p.Key.Model, Totals(p.Value)))
            .OrderBy(m => m.Provider).ThenBy(m => m.Model)
            .ToList();
        var all = new AiUsageTotals(
            byModel.Sum(m => m.Totals.Requests), byModel.Sum(m => m.Totals.InputTokens), byModel.Sum(m => m.Totals.OutputTokens),
            byModel.Sum(m => m.Totals.CacheCreationInputTokens), byModel.Sum(m => m.Totals.CacheReadInputTokens));
        return new AiUsageSnapshot(_since, all, byModel, _recent.Reverse().ToList());
    }

    private static AiUsageTotals Totals(Counter c) => new(
        Interlocked.Read(ref c.Requests), Interlocked.Read(ref c.Input), Interlocked.Read(ref c.Output),
        Interlocked.Read(ref c.CacheCreation), Interlocked.Read(ref c.CacheRead));
}
