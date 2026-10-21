using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Persistence;
using AiReap.Domain.Common;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AiReap.Tests.GroundedGenerationTests;

namespace AiReap.Tests;

// P0-2 — the v4 requirement generation prompt still carries only its own stage's routing keyword (the
// development stub, and anything else that routes on it, still answers it as requirement generation),
// and clarification codes are added to the generation prompt only: every other stage's clarification
// list is unchanged.
public class PromptRoutingTests
{
    private sealed class RecordingAiChatClient : IAiChatClient
    {
        private readonly StubAiChatClient _inner = new();
        public List<(string System, string User)> Calls { get; } = new();
        public string ModelName => _inner.ModelName;

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            lock (Calls) Calls.Add((systemPrompt, userPrompt));
            return _inner.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
        }
    }

    // StubAiChatClient's routing keywords for every stage but requirement generation.
    private static readonly string[] OtherStageKeywords =
    [
        "readinessSummary", "missingInformation", "userStories", "acceptanceCriteria", "businessRules", "relationshipType",
        "artifactCode", "architectureOverview", "dataEntities", "apiSpecifications", "taskType", "testCases", "citedChunkRefs"
    ];

    [Fact]
    public async Task The_v4_generation_prompt_routes_only_as_requirement_generation()
    {
        var ai = new RecordingAiChatClient();
        await using var host = new TestHost(chatClient: ai);
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (_, sourceId) = await SourceAsync(ba);

        var (status, result) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(200, status);
        Assert.Equal("Submit request", result!["functionalRequirements"]![0]!["title"]!.GetValue<string>()); // the stub's requirements answer
        var system = Assert.Single(ai.Calls).System;
        Assert.Contains("functionalRequirements", system);
        Assert.All(OtherStageKeywords, k => Assert.DoesNotContain(k, system));
        Assert.Contains("requirementOrigin", system);
        Assert.Contains("evidence", system);
        Assert.Contains("targetOrigin", system);
        Assert.DoesNotContain("assumptionStatus", system); // decided by the server, not asked of the model

        // Stub output has no evidence: stored as checked, with nothing found and nothing invented.
        var data = result["functionalRequirements"]![0]!["data"]!;
        Assert.Equal("none", data["evidenceStatus"]!.GetValue<string>());
        Assert.Empty(data["evidence"]!.AsArray());
        Assert.Null(data["requirementOrigin"]);
    }

    [Fact]
    public async Task Clarification_codes_are_listed_for_requirement_generation_only()
    {
        var ai = new RecordingAiChatClient();
        await using var host = new TestHost(chatClient: ai);
        var ba = await host.RegisterAsync(Roles.BusinessAnalyst);
        var (projectId, sourceId) = await SourceAsync(ba);
        await AnalyzeAndAnswerAsync(ba, projectId, sourceId);

        Assert.Equal(200, (await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);
        var generation = ai.Calls.Single(c => c.System.Contains("requirementOrigin")).User;
        Assert.Contains($"- CQ-001 Q: Can approval be delegated, and is multi-level approval required?\n  A: {Answer}\n", generation);
        Assert.Contains("- CQ-002 Q: Should the submitter be notified when the request is approved or rejected?\n" +
                        "  A: (not yet answered - anything that depends on it is assumed: cite CQ-002 in assumedClarifications)\n", generation);

        // Every other stage builds the list through BuildAsync / BuildItemsAsync without codes - unchanged.
        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAiReapDbContext>();
        var id = Guid.Parse(sourceId);
        var expected =
            "\n\nClarifications gathered so far:\n" +
            $"- Q: Can approval be delegated, and is multi-level approval required?\n  A: {Answer}\n" +
            "- Q: Should the submitter be notified when the request is approved or rejected?\n" +
            "  A: (not yet answered - treat as still unresolved, propose an assumption if needed and flag it)\n";
        Assert.Equal(expected, await ClarificationContextBuilder.BuildAsync(db, id, CancellationToken.None));
        Assert.Equal(expected[ClarificationContextBuilder.Heading.Length..],
            string.Concat(await ClarificationContextBuilder.BuildItemsAsync(db, id, CancellationToken.None)));
    }
}
