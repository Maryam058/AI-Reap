using AiReap.Application.Ai;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiReap.Tests;

// Token usage tracking without a database change: the Anthropic client reports each response's usage
// to an in-memory meter (AiUsageMeter), also for a response it then rejects, since that is billed.
// Offline: a fake HTTP handler stands in for the Anthropic API; no request leaves the machine.
public class AiUsageMeterTests
{
    private static AnthropicAiChatClient Client(object body, IAiUsageMeter meter) =>
        new(new HttpClient(new AnthropicAiChatClientTests.FakeHandler(body)) { BaseAddress = new Uri("https://api.anthropic.com/") },
            Options.Create(new AnthropicOptions { ApiKey = AnthropicAiChatClientTests.FakeKey, Model = "claude-sonnet-5-5", MaxTokens = 8192 }),
            meter);

    private static object Response(string stopReason, int input, int output, int cacheRead = 0) => new
    {
        model = "claude-sonnet-5-5",
        content = new[] { new { type = "text", text = "{}" } },
        stop_reason = stopReason,
        usage = new { input_tokens = input, output_tokens = output, cache_read_input_tokens = cacheRead }
    };

    [Fact]
    public async Task Each_response_adds_its_reported_usage_per_model()
    {
        var meter = new AiUsageMeter();

        await Client(Response("end_turn", 1200, 300, cacheRead: 1000), meter).CompleteAsync("s", "u");
        await Client(Response("end_turn", 800, 200), meter).CompleteAsync("s", "u");

        var snapshot = meter.Snapshot();
        Assert.Equal(new AiUsageTotals(2, 2000, 500, 0, 1000), snapshot.Totals);
        var model = Assert.Single(snapshot.ByModel);
        Assert.Equal(("Anthropic", "claude-sonnet-5-5"), (model.Provider, model.Model));
        Assert.Equal(2, snapshot.Recent.Count);
    }

    [Fact]
    public async Task A_truncated_response_is_rejected_but_its_billed_usage_is_still_counted()
    {
        var meter = new AiUsageMeter();

        await Assert.ThrowsAsync<AiOutputTruncatedException>(() => Client(Response("max_tokens", 5000, 8192), meter).CompleteAsync("s", "u"));

        Assert.Equal(8192, meter.Snapshot().Totals.OutputTokens);
    }

    [Fact]
    public async Task A_response_without_usage_counts_nothing_and_the_client_works_without_a_meter()
    {
        var meter = new AiUsageMeter();
        var noUsage = new { model = "claude-sonnet-5-5", content = new[] { new { type = "text", text = "{}" } }, stop_reason = "end_turn" };

        await Client(noUsage, meter).CompleteAsync("s", "u");
        var withoutMeter = new AnthropicAiChatClient(
            new HttpClient(new AnthropicAiChatClientTests.FakeHandler(Response("end_turn", 1, 1))) { BaseAddress = new Uri("https://api.anthropic.com/") },
            Options.Create(new AnthropicOptions { ApiKey = AnthropicAiChatClientTests.FakeKey, Model = "claude-sonnet-5-5" }));

        Assert.Equal("{}", await withoutMeter.CompleteAsync("s", "u"));
        Assert.Equal(0, meter.Snapshot().Totals.Requests);
    }
}
