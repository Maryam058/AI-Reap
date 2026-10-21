using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Application.Artifacts;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiReap.Tests;

// Phase 2: batched (resumable) runs over ONE requirement source (AiChunkRunService), stable source
// IDs and dependency links, database-level duplicate protection (ArtifactFingerprints), concurrent
// code assignment, atomic persistence, and batch mode off = unchanged behaviour. Real Gemini/Groq
// clients over fake HTTP and synthetic sources only - never a live API; throwaway test databases.
public class AiChunkRunTests
{
    private const string GeminiKey = "AIzaSyTESTTESTTESTTESTTESTTESTTESTTEST12";
    private const string GroqKey = "gsk_TESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTEST";
    private const int GroqMaxChunkedParts = 8;
    private static readonly Dictionary<string, string> BatchOn = new() { ["Ai:Groq:MaxBatchedRunParts"] = "80" };

    // ~30k request tokens: 10-14 Groq parts for analysis - more than 8, so two Continue actions.
    private const int SourceTokens = 30_000;

    private static readonly Regex PartPattern = new(@"This is part (\d+) of (\d+)\.");
    private static readonly Regex ReqId = new(@"REQ-\d+\.\d+");

    // ---- fake providers ------------------------------------------------------------------

    private sealed class Http(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Bodies { get; } = new();
        public Func<HttpRequestMessage, string, Task<HttpResponseMessage>> Respond { get; set; } = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Enqueue(body);
            return await Respond(request, body);
        }
    }

    private sealed record Stack(FallbackAiChatClient Client, Http Gemini, Http Groq, AiProviderCooldown Cooldown);

    // The production chain over fake HTTP. Gemini is put in a recorded cool-down unless
    // geminiAvailable, so parts go straight to Groq (the situation batch mode exists for).
    private static Stack BuildStack(
        Func<HttpRequestMessage, string, Task<HttpResponseMessage>>? groq = null,
        Func<HttpRequestMessage, string, Task<HttpResponseMessage>>? gemini = null, bool geminiAvailable = false)
    {
        var geminiHttp = new Http(gemini ?? ((_, _) => throw new InvalidOperationException("Gemini must not be called.")));
        var groqHttp = new Http(groq ?? ((_, _) => throw new InvalidOperationException("Groq must not be called.")));
        var resilience = new AiResilienceOptions { BaseDelayMilliseconds = 0 };
        Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;
        var cooldown = new AiProviderCooldown();
        if (!geminiAvailable) cooldown.CoolDown("Gemini", DateTimeOffset.UtcNow.AddHours(1));

        var client = new FallbackAiChatClient(
            new ResilientAiChatClient(new GeminiAiChatClient(new HttpClient(geminiHttp), Options.Create(new GeminiOptions { ApiKey = GeminiKey })),
                "Gemini", resilience.WithoutRateLimitRetry(), NullLogger.Instance, NoDelay), "Gemini",
            new RequestBudgetAiChatClient(
                new ResilientAiChatClient(new GroqAiChatClient(new HttpClient(groqHttp), Options.Create(new GroqOptions { ApiKey = GroqKey })),
                    "Groq", resilience, NullLogger.Instance, NoDelay), "Groq",
                "Ai:Groq:MaxRequestTokens", 8000, 4000, NullLogger.Instance, GroqMaxChunkedParts, "Ai:Groq:MaxChunkedParts"), "Groq",
            cooldown, TimeSpan.FromHours(1), NullLogger.Instance);
        return new Stack(client, geminiHttp, groqHttp, cooldown);
    }

    private static string UserPrompt(string groqBody) =>
        JsonNode.Parse(groqBody)!["messages"]![1]!["content"]!.GetValue<string>();

    private static int PartOf(string groqBody) =>
        PartPattern.Match(UserPrompt(groqBody)) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    private static Task<HttpResponseMessage> GroqOk(string content) =>
        Task.FromResult(GroqAiChatClientTests.Json(GroqAiChatClientTests.Success(content)));

    private static Task<HttpResponseMessage> GeminiOk(string text) => Task.FromResult(GroqAiChatClientTests.Json(new
    {
        candidates = new[] { new { content = new { role = "model", parts = new[] { new { text } } }, finishReason = "STOP" } }
    }));

    private static Task<HttpResponseMessage> GroqDailyQuota()
    {
        var response = GroqAiChatClientTests.Json(new
        {
            error = new
            {
                message = "Rate limit reached for model `openai/gpt-oss-120b` service tier `on_demand` on tokens per day (TPD): " +
                          "Limit 200000, Used 199500, Requested 7800. Please try again in 1h0m0s.",
                type = "tokens",
                code = "rate_limit_exceeded"
            }
        }, HttpStatusCode.TooManyRequests);
        response.Headers.Add("retry-after", "3600");
        return Task.FromResult(response);
    }

    // Analysis of one part: three questions specific to it.
    private static string PartAnalysis(int part) => new JsonObject
    {
        ["actors"] = new JsonArray("Employee"),
        ["capabilities"] = new JsonArray("Request leave"),
        ["dataElements"] = new JsonArray(),
        ["notes"] = new JsonArray(),
        ["missingInformation"] = new JsonArray(Enumerable.Range(1, 3).Select(k => (JsonNode)new JsonObject
        {
            ["topic"] = $"Area {part} rule {k}",
            ["question"] = $"Which approver handles rule {k} of part {part}?",
            ["reason"] = "Not stated"
        }).ToArray())
    }.ToJsonString();

    // Generation of one part: one FR citing the first requirement ID in the part text.
    private static string PartGeneration(string groqBody)
    {
        var user = UserPrompt(groqBody);
        var id = ReqId.Match(user) is { Success: true } m ? m.Value : null;
        return new JsonObject
        {
            ["functionalRequirements"] = new JsonArray(new JsonObject
            {
                ["title"] = $"Record leave for part {PartOf(groqBody)}",
                ["actor"] = "HR",
                ["priority"] = "Medium",
                ["sourceIds"] = id is null ? new JsonArray() : new JsonArray(id)
            }),
            ["nonFunctionalRequirements"] = new JsonArray()
        }.ToJsonString();
    }

    private static async Task<(string ProjectId, string SourceId)> CreateSourceAsync(ApiUser ba, string rawText, string name = "Batched SRS")
    {
        var (ps, project) = await ba.PostAsync("/api/projects", new { name, description = "d" });
        Assert.Equal(201, ps);
        var projectId = project!["id"]!.GetValue<string>();
        var (ss, created) = await ba.PostAsync($"/api/projects/{projectId}/requirement-sources", new { sourceType = 1, rawText });
        Assert.InRange(ss, 200, 201);
        return (projectId, created!["id"]!.GetValue<string>());
    }

    private static async Task<string> AddSourceAsync(ApiUser ba, string projectId, string rawText)
    {
        var (ss, created) = await ba.PostAsync($"/api/projects/{projectId}/requirement-sources", new { sourceType = 1, rawText });
        Assert.InRange(ss, 200, 201);
        return created!["id"]!.GetValue<string>();
    }

    private static async Task<JsonArray> ArtifactsAsync(ApiUser user, string projectId, int? type = null)
    {
        var all = (await user.GetAsync($"/api/projects/{projectId}/artifacts")).Body!.AsArray();
        return new JsonArray(all.Where(a => type is null || a!["artifactType"]!.GetValue<int>() == type).Select(a => a!.DeepClone()).ToArray());
    }

    private const int Fr = 0, Nfr = 1, Cq = 5, Bo = 11;

    private static async Task<JsonNode> StartAsync(ApiUser ba, string sourceId, string operation, int expectedStatus = 201)
    {
        var (status, run) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/ai-runs", new { operation });
        Assert.True(expectedStatus == status, $"Start returned {status}: {run?.ToJsonString()}");
        return run!;
    }

    private static string Id(JsonNode run) => run["id"]!.GetValue<string>();
    private static string Status(JsonNode run) => run["status"]!.GetValue<string>();
    private static int Int(JsonNode node, string name) => node[name]!.GetValue<int>();

    // ---- batch mode off: unchanged -------------------------------------------------------

    [Fact]
    public async Task Batch_mode_off_refuses_run_endpoints_and_never_touches_the_run_tables()
    {
        var stack = BuildStack(groq: (_, _) => throw new InvalidOperationException("Groq must not be called."));
        await using var host = new TestHost(chatClient: stack.Client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));

        // Proof the tables are never read with batch mode off: they do not exist in this database.
        await host.ExecuteAsync("DROP TABLE AiChunkRunParts; DROP TABLE AiChunkRuns;");

        var (startStatus, problem) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/ai-runs", new { operation = "analyze" });
        Assert.Equal(400, startStatus);
        Assert.Equal("batch_mode_disabled", problem!["code"]!.GetValue<string>());
        Assert.Equal(400, (await ba.GetAsync($"/api/requirement-sources/{sourceId}/ai-runs?operation=analyze")).Status);
        Assert.Equal(400, (await ba.PostAsync($"/api/ai-runs/{Guid.NewGuid()}/continue")).Status);

        var (planStatus, plan) = await ba.GetAsync($"/api/requirement-sources/{sourceId}/ai-plan?operation=analyze");
        Assert.Equal(200, planStatus);
        Assert.False(plan!["batch"]!["enabled"]!.GetValue<bool>());

        // The normal path is refused exactly as before, before any Groq request.
        var (status, refusal) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");
        Assert.Equal(502, status);
        Assert.Equal("ai_request_rejected", refusal!["code"]!.GetValue<string>());
        Assert.Matches(@"it would need \d+ parts; Ai:Groq:MaxChunkedParts=8", refusal["detail"]!.GetValue<string>());
        Assert.Empty(stack.Groq.Bodies);
        Assert.Empty(await ArtifactsAsync(ba, projectId));
    }

    // ---- Start / Continue / Commit -------------------------------------------------------

    [Fact]
    public async Task Batched_analysis_sends_at_most_eight_parts_per_continue_and_commits_once()
    {
        var stack = BuildStack(groq: (_, body) => GroqOk(PartAnalysis(PartOf(body))));
        await using var host = new TestHost(chatClient: stack.Client, settings: BatchOn);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));

        var (_, plan) = await ba.GetAsync($"/api/requirement-sources/{sourceId}/ai-plan?operation=analyze");
        var batch = plan!["batch"]!;
        Assert.True(batch["canStart"]!.GetValue<bool>());
        Assert.Equal(8, Int(batch, "partsPerAction"));

        var run = await StartAsync(ba, sourceId, "analyze");
        var count = Int(run, "partCount");
        Assert.Equal(Int(batch, "parts"), count); // Start froze the plan the AI plan reported
        Assert.InRange(count, 9, 16);
        Assert.Equal("Running", Status(run));
        Assert.Empty(stack.Groq.Bodies); // Start sends nothing

        var (s1, first) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue");
        Assert.Equal(200, s1);
        Assert.Equal(8, stack.Groq.Bodies.Count);
        Assert.Equal(8, Int(first!, "succeededParts"));
        Assert.Equal("Running", Status(first!));
        Assert.Empty(await ArtifactsAsync(ba, projectId)); // nothing before Commit

        var (_, second) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue");
        Assert.Equal(count, stack.Groq.Bodies.Count);
        Assert.Equal("Staged", Status(second!));
        Assert.Equal(Enumerable.Range(1, count), stack.Groq.Bodies.Select(PartOf).Order()); // each part once

        var (cs, committed) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/commit");
        Assert.Equal(200, cs);
        Assert.Equal("Committed", Status(committed!));
        var questions = await ArtifactsAsync(ba, projectId, Cq);
        Assert.Equal(count * 3, questions.Count);
        Assert.Equal(count * 3, committed!["result"]!["createdArtifactIds"]!.AsArray().Count);
        Assert.Single((await ba.GetAsync($"/api/projects/{projectId}/requirement-sources")).Body!.AsArray()); // still one source

        // Working state is cleared; the raw responses live in the audit trail.
        Assert.Equal(0, await host.ScalarAsync("SELECT COUNT(*) FROM AiChunkRunParts WHERE PromptText IS NOT NULL OR RawResponse IS NOT NULL OR ParsedJson IS NOT NULL"));
        var (_, audit) = await ba.GetAsync($"/api/projects/{projectId}/ai-executions");
        Assert.Contains(audit!.AsArray(), e => e!["operationType"]!.GetValue<string>() == "RequirementAnalysis");

        // Committing again returns the same result and creates nothing.
        var (again, repeat) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/commit");
        Assert.Equal(200, again);
        Assert.Equal(committed["result"]!.ToJsonString(), repeat!["result"]!.ToJsonString());
        Assert.Equal(count * 3, (await ArtifactsAsync(ba, projectId, Cq)).Count);
        Assert.Equal(count, stack.Groq.Bodies.Count);
    }

    [Fact]
    public async Task Quota_failure_keeps_finished_parts_and_continue_never_resends_them()
    {
        var failOnce = 1;
        var stack = BuildStack(groq: (_, body) =>
            PartOf(body) == 4 && Interlocked.Exchange(ref failOnce, 0) == 1 ? GroqDailyQuota() : GroqOk(PartAnalysis(PartOf(body))));
        await using var host = new TestHost(chatClient: stack.Client, settings: BatchOn);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));
        var run = await StartAsync(ba, sourceId, "analyze");
        var count = Int(run, "partCount");

        var (_, interrupted) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue");
        Assert.Equal("Running", Status(interrupted!));
        Assert.Equal(3, Int(interrupted!, "succeededParts"));
        Assert.Equal(1, Int(interrupted!, "failedParts"));
        Assert.True(interrupted!["lastErrorRetryable"]!.GetValue<bool>());
        Assert.Contains("tokens per day", interrupted!["lastError"]!.GetValue<string>());
        Assert.Empty(await ArtifactsAsync(ba, projectId));

        while (Status((await ba.GetAsync($"/api/ai-runs/{Id(run)}")).Body!) == "Running")
        {
            Assert.Equal(200, (await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue")).Status);
        }

        var sent = stack.Groq.Bodies.Select(PartOf).ToList();
        Assert.Equal([1, 2, 3], sent.Where(p => p <= 3).Order()); // parts 1-3 never re-sent
        Assert.Equal(2, sent.Count(p => p == 4)); // the failed part was retried once
        Assert.Equal(count + 1, sent.Count);
        Assert.Equal(200, (await ba.PostAsync($"/api/ai-runs/{Id(run)}/commit")).Status);
        Assert.Equal(count * 3, (await ArtifactsAsync(ba, projectId, Cq)).Count);
    }

    [Fact]
    public async Task Invalid_part_output_is_retried_and_fails_the_run_after_three_attempts()
    {
        var stack = BuildStack(groq: (_, body) => PartOf(body) == 2 ? GroqOk("{\"actors\": \"not a list\"}") : GroqOk(PartAnalysis(PartOf(body))));
        await using var host = new TestHost(chatClient: stack.Client, settings: BatchOn);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));
        var run = await StartAsync(ba, sourceId, "analyze");

        JsonNode state = run;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (_, body) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue");
            state = body!;
            Assert.Equal(attempt, state["parts"]!.AsArray().Single(p => Int(p!, "number") == 2)!["attempts"]!.GetValue<int>());
        }

        Assert.Equal("Failed", Status(state!));
        Assert.Equal(409, (await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue")).Status);
        Assert.Equal(409, (await ba.PostAsync($"/api/ai-runs/{Id(run)}/commit")).Status);
        Assert.Empty(await ArtifactsAsync(ba, projectId));

        // A failed run is no longer active: a new one can start.
        await StartAsync(ba, sourceId, "analyze");
    }

    // ---- concurrency -----------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_starts_create_one_active_run()
    {
        var stack = BuildStack();
        await using var host = new TestHost(chatClient: stack.Client, settings: BatchOn);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (_, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            ba.PostAsync($"/api/requirement-sources/{sourceId}/ai-runs", new { operation = "analyze" })));

        var created = Assert.Single(results, r => r.Status == 201);
        Assert.All(results.Where(r => r.Status != 201), r =>
        {
            Assert.Equal(409, r.Status);
            Assert.Equal("ai_batch_run_active", r.Body!["code"]!.GetValue<string>());
            Assert.Equal(Id(created.Body!), r.Body!["activeRunId"]!.GetValue<string>());
        });
        Assert.Equal(1, await host.ScalarAsync("SELECT COUNT(*) FROM AiChunkRuns"));
    }

    [Fact]
    public async Task Concurrent_continue_is_refused_while_a_run_is_being_processed()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var stack = BuildStack(groq: async (_, body) =>
        {
            if (PartOf(body) == 1)
            {
                entered.TrySetResult();
                await gate.Task;
            }

            return await GroqOk(PartAnalysis(PartOf(body)));
        });
        await using var host = new TestHost(chatClient: stack.Client, settings: BatchOn);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (_, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));
        var run = await StartAsync(ba, sourceId, "analyze");

        var first = ba.PostAsync($"/api/ai-runs/{Id(run)}/continue");
        await entered.Task;
        var (status, problem) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue");
        Assert.Equal(409, status);
        Assert.Contains("being processed", problem!["detail"]!.GetValue<string>());
        Assert.Equal(409, (await ba.PostAsync($"/api/ai-runs/{Id(run)}/abandon")).Status);
        gate.SetResult();

        Assert.Equal(200, (await first).Status);
        var sent = stack.Groq.Bodies.Select(PartOf).ToList();
        Assert.Equal(sent.Count, sent.Distinct().Count()); // no part sent twice
        Assert.Equal(8, sent.Count);
    }

    [Fact]
    public async Task Concurrent_commits_save_the_artifacts_once()
    {
        var stack = BuildStack(groq: (_, body) => GroqOk(PartAnalysis(PartOf(body))));
        await using var host = new TestHost(chatClient: stack.Client, settings: BatchOn);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));
        var run = await StartAsync(ba, sourceId, "analyze");
        await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue");
        await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue");

        var commits = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => ba.PostAsync($"/api/ai-runs/{Id(run)}/commit")));

        Assert.Contains(commits, c => c.Status == 200);
        Assert.All(commits, c => Assert.True(c.Status is 200 or 409, $"commit returned {c.Status}"));
        var count = Int(run, "partCount");
        Assert.Equal(count * 3, (await ArtifactsAsync(ba, projectId, Cq)).Count);
        Assert.Equal(count * 3, await host.ScalarAsync("SELECT COUNT(*) FROM ArtifactFingerprints"));
        Assert.Single(commits.Where(c => c.Status == 200).Select(c => c.Body!["result"]!.ToJsonString()).Distinct());
    }

    // ---- stale source / context ------------------------------------------------------------

    [Fact]
    public async Task A_changed_source_text_supersedes_the_run_and_saves_nothing()
    {
        var stack = BuildStack(groq: (_, body) => GroqOk(PartAnalysis(PartOf(body))));
        await using var host = new TestHost(chatClient: stack.Client, settings: BatchOn);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));
        var run = await StartAsync(ba, sourceId, "analyze");
        await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue");
        var sent = stack.Groq.Bodies.Count;

        await host.ExecuteAsync($"UPDATE RequirementSources SET RawText = RawText + N' Amended.' WHERE Id = '{sourceId}'");

        var (status, problem) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue");
        Assert.Equal(409, status);
        Assert.Contains("changed after this batched run started", problem!["detail"]!.GetValue<string>());
        Assert.Equal(sent, stack.Groq.Bodies.Count);
        Assert.Equal("Superseded", Status((await ba.GetAsync($"/api/ai-runs/{Id(run)}")).Body!));
        Assert.Equal(409, (await ba.PostAsync($"/api/ai-runs/{Id(run)}/commit")).Status);
        Assert.Empty(await ArtifactsAsync(ba, projectId));
        Assert.Equal(0, await host.ScalarAsync("SELECT COUNT(*) FROM AiChunkRunParts WHERE PromptText IS NOT NULL OR RawResponse IS NOT NULL"));
    }

    [Fact]
    public async Task Changed_objectives_supersede_a_staged_generation_at_commit()
    {
        var stack = BuildStack(groq: (_, body) => GroqOk(PartGeneration(body)));
        await using var host = new TestHost(chatClient: stack.Client, settings: BatchOn);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));
        var run = await StartAsync(ba, sourceId, "generate");
        while (Status((await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue")).Body!) == "Running") { }

        await host.ExecuteAsync($"UPDATE Projects SET Objectives = N'Cut triage time' WHERE Id = '{projectId}'");

        var (status, _) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/commit");
        Assert.Equal(409, status);
        Assert.Equal("Superseded", Status((await ba.GetAsync($"/api/ai-runs/{Id(run)}")).Body!));
        Assert.Empty(await ArtifactsAsync(ba, projectId));
    }

    // ---- atomic commit ---------------------------------------------------------------------

    [Fact]
    public async Task A_commit_that_fails_to_save_writes_nothing_and_can_be_retried()
    {
        var stack = BuildStack(groq: (_, body) => GroqOk(PartGeneration(body)));
        await using var host = new TestHost(chatClient: stack.Client, settings: BatchOn);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));
        await host.ExecuteAsync($"UPDATE Projects SET Objectives = N'Cut leave admin time' WHERE Id = '{projectId}'"); // not yet recorded
        var run = await StartAsync(ba, sourceId, "generate");
        while (Status((await ba.PostAsync($"/api/ai-runs/{Id(run)}/continue")).Body!) == "Running") { }
        var auditBefore = await host.ScalarAsync("SELECT COUNT(*) FROM AIExecutions");

        // The throwaway test database rejects every new artifact version: the commit's save fails.
        await host.ExecuteAsync("ALTER TABLE ArtifactVersions WITH NOCHECK ADD CONSTRAINT CK_TestRejectVersions CHECK (1 = 0)");
        var (failed, _) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/commit");
        Assert.Equal(500, failed);
        Assert.Empty(await ArtifactsAsync(ba, projectId)); // no requirement, no objective
        Assert.Equal(0, await host.ScalarAsync("SELECT COUNT(*) FROM ArtifactFingerprints"));
        Assert.Equal(0, await host.ScalarAsync("SELECT COUNT(*) FROM ArtifactRelationships"));
        Assert.Equal(auditBefore, await host.ScalarAsync("SELECT COUNT(*) FROM AIExecutions"));
        Assert.Equal("Staged", Status((await ba.GetAsync($"/api/ai-runs/{Id(run)}")).Body!));

        await host.ExecuteAsync("ALTER TABLE ArtifactVersions DROP CONSTRAINT CK_TestRejectVersions");
        var (ok, committed) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/commit");
        Assert.Equal(200, ok);
        Assert.Equal("Committed", Status(committed!));
        Assert.Equal(Int(run, "partCount"), (await ArtifactsAsync(ba, projectId, Fr)).Count);
        Assert.Single(await ArtifactsAsync(ba, projectId, Bo)); // recorded with the requirements
    }

    // ---- duplicates --------------------------------------------------------------------------

    private static string WholeGeneration(params (string Title, string[] SourceIds, string[] DependsOn)[] frs) => new JsonObject
    {
        ["functionalRequirements"] = new JsonArray(frs.Select(f => (JsonNode)new JsonObject
        {
            ["title"] = f.Title,
            ["actor"] = "Employee",
            ["priority"] = "High",
            ["sourceIds"] = new JsonArray(f.SourceIds.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["dependsOnSourceIds"] = new JsonArray(f.DependsOn.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
        }).ToArray()),
        ["nonFunctionalRequirements"] = new JsonArray(new JsonObject
        {
            ["title"] = "Leave records retained", ["category"] = "Compliance", ["description"] = "Seven years"
        })
    }.ToJsonString();

    private const string SmallSrs =
        "## 1. Leave requests\n\nREQ-1.1: Employees must be able to request leave.\n\n" +
        "## 2. Approval\n\nREQ-2.1: Managers must approve leave requests submitted under REQ-1.1.";

    [Fact]
    public async Task Rerunning_generation_saves_no_duplicates_and_links_dependencies_by_source_id()
    {
        var output = WholeGeneration(("Request leave", ["REQ-1.1"], []), ("Approve leave", ["REQ-2.1"], ["REQ-1.1"]));
        var stack = BuildStack(gemini: (_, _) => GeminiOk(output), geminiAvailable: true);
        await using var host = new TestHost(chatClient: stack.Client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, SmallSrs);

        var (s1, first) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, s1);
        Assert.Equal(2, first!["functionalRequirements"]!.AsArray().Count);
        Assert.Equal(0, Int(first, "skippedDuplicates"));

        var approve = first["functionalRequirements"]!.AsArray().Single(a => a!["title"]!.GetValue<string>() == "Approve leave")!;
        Assert.Equal(["REQ-2.1"], approve["data"]!["sourceIds"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("## 2. Approval", approve["data"]!["sourceSection"]!.GetValue<string>());
        var request = first["functionalRequirements"]!.AsArray().Single(a => a!["title"]!.GetValue<string>() == "Request leave")!;
        // Approve leave (REQ-2.1) -DependsOn(3)-> Request leave (REQ-1.1), and nothing else.
        Assert.Equal(1, await host.ScalarAsync(
            $"SELECT COUNT(*) FROM ArtifactRelationships WHERE RelationshipType = 3 AND SourceArtifactId = '{approve["id"]!.GetValue<string>()}' " +
            $"AND TargetArtifactId = '{request["id"]!.GetValue<string>()}'"));
        Assert.Equal(1, await host.ScalarAsync("SELECT COUNT(*) FROM ArtifactRelationships WHERE RelationshipType = 3"));

        var (s2, second) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, s2);
        Assert.Empty(second!["functionalRequirements"]!.AsArray());
        Assert.Empty(second["nonFunctionalRequirements"]!.AsArray());
        Assert.Equal(3, Int(second, "skippedDuplicates"));
        Assert.Equal(2, (await ArtifactsAsync(ba, projectId, Fr)).Count);
        Assert.Single(await ArtifactsAsync(ba, projectId, Nfr));
    }

    [Fact]
    public async Task Rerunning_analysis_saves_no_duplicate_clarification_questions()
    {
        var stack = BuildStack(gemini: (_, _) => GeminiOk(PartAnalysis(1)), geminiAvailable: true);
        await using var host = new TestHost(chatClient: stack.Client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, SmallSrs);

        Assert.Equal(200, (await ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze")).Status);
        var (_, again) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");

        Assert.Empty(again!["clarificationQuestions"]!.AsArray());
        Assert.Equal(3, Int(again, "skippedDuplicates"));
        Assert.Equal(3, (await ArtifactsAsync(ba, projectId, Cq)).Count);
    }

    [Fact]
    public async Task Concurrent_generations_of_the_same_source_save_each_requirement_once()
    {
        var output = WholeGeneration(("Request leave", ["REQ-1.1"], []), ("Approve leave", ["REQ-2.1"], []));
        var stack = BuildStack(gemini: async (_, _) => { await Task.Delay(50); return await GeminiOk(output); }, geminiAvailable: true);
        await using var host = new TestHost(chatClient: stack.Client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, SmallSrs);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")));

        Assert.All(results, r => Assert.True(r.Status is 200 or 409, $"generate returned {r.Status}: {r.Body?.ToJsonString()}"));
        Assert.Equal(2, (await ArtifactsAsync(ba, projectId, Fr)).Count);
        Assert.Single(await ArtifactsAsync(ba, projectId, Nfr));
        Assert.Equal(3, await host.ScalarAsync("SELECT COUNT(*) FROM ArtifactFingerprints"));
    }

    [Fact]
    public async Task Identical_content_in_another_source_and_concurrent_runs_get_distinct_codes()
    {
        var output = WholeGeneration(("Request leave", [], []));
        var stack = BuildStack(gemini: async (_, _) => { await Task.Delay(50); return await GeminiOk(output); }, geminiAvailable: true);
        await using var host = new TestHost(chatClient: stack.Client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, first) = await CreateSourceAsync(ba, "Employees must be able to request leave.");
        var second = await AddSourceAsync(ba, projectId, "Employees must be able to request leave, again.");
        var third = await AddSourceAsync(ba, projectId, "Employees must be able to request leave, once more.");

        var results = await Task.WhenAll(new[] { first, second, third }.Select(s =>
            ba.PostAsync($"/api/requirement-sources/{s}/generate-requirements")));

        Assert.All(results, r => Assert.Equal(200, r.Status));
        var frs = await ArtifactsAsync(ba, projectId, Fr);
        Assert.Equal(3, frs.Count); // same content, three sources: all legitimate
        Assert.Equal(3, frs.Select(a => a!["code"]!.GetValue<string>()).Distinct().Count());
    }

    [Fact]
    public async Task Source_ids_not_in_the_text_are_rejected_and_nothing_is_saved()
    {
        var output = WholeGeneration(("Request leave", ["REQ-9.9"], []));
        var stack = BuildStack(gemini: (_, _) => GeminiOk(output), geminiAvailable: true);
        await using var host = new TestHost(chatClient: stack.Client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, SmallSrs);

        var (status, problem) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");

        Assert.Equal(502, status);
        Assert.Equal("ai_invalid_output", problem!["code"]!.GetValue<string>());
        Assert.Contains(problem["errors"]!.AsArray(), e => e!.GetValue<string>().Contains("REQ-9.9"));
        Assert.Equal(2, stack.Gemini.Bodies.Count); // the request and its one repair
        Assert.Empty(await ArtifactsAsync(ba, projectId));
    }

    [Fact]
    public async Task A_failed_generation_records_no_business_objectives()
    {
        var stack = BuildStack(gemini: (_, _) => GeminiOk("{\"functionalRequirements\": []}"), geminiAvailable: true);
        await using var host = new TestHost(chatClient: stack.Client);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, SmallSrs);
        await host.ExecuteAsync($"UPDATE Projects SET Objectives = N'Cut leave admin time' WHERE Id = '{projectId}'");

        Assert.Equal(502, (await ba.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements")).Status);
        Assert.Empty(await ArtifactsAsync(ba, projectId));
    }

    // ---- limits and schema -------------------------------------------------------------------

    [Fact]
    public async Task A_source_needing_more_parts_than_MaxBatchedRunParts_cannot_start()
    {
        var stack = BuildStack();
        await using var host = new TestHost(chatClient: stack.Client, settings: new Dictionary<string, string> { ["Ai:Groq:MaxBatchedRunParts"] = "5" });
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var (_, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));

        var (status, problem) = await ba.PostAsync($"/api/requirement-sources/{sourceId}/ai-runs", new { operation = "analyze" });

        Assert.Equal(400, status);
        Assert.Equal("ai_batch_too_many_parts", problem!["code"]!.GetValue<string>());
        Assert.Contains("Ai:Groq:MaxBatchedRunParts=5", problem["detail"]!.GetValue<string>());
        var (_, plan) = await ba.GetAsync($"/api/requirement-sources/{sourceId}/ai-plan?operation=analyze");
        Assert.False(plan!["batch"]!["canStart"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Only_writers_run_steps_and_members_read_runs()
    {
        var stack = BuildStack();
        await using var host = new TestHost(chatClient: stack.Client, settings: BatchOn);
        var ba = await host.RegisterAsync("BusinessAnalyst");
        var reviewer = await host.RegisterAsync("Reviewer");
        var outsider = await host.RegisterAsync("BusinessAnalyst");
        var (projectId, sourceId) = await CreateSourceAsync(ba, ChunkedAnalysisTests.StructuredSource(SourceTokens));
        Assert.InRange((await host.AddMemberAsync(ba, projectId, reviewer)).Status, 200, 201);
        var run = await StartAsync(ba, sourceId, "analyze");

        Assert.Equal(403, (await reviewer.PostAsync($"/api/ai-runs/{Id(run)}/continue")).Status);
        Assert.Equal(200, (await reviewer.GetAsync($"/api/ai-runs/{Id(run)}")).Status);
        Assert.Equal(403, (await outsider.GetAsync($"/api/ai-runs/{Id(run)}")).Status);
        Assert.Equal(403, (await outsider.PostAsync($"/api/ai-runs/{Id(run)}/abandon")).Status);
        Assert.Equal(200, (await reviewer.GetAsync($"/api/requirement-sources/{sourceId}/ai-runs?operation=analyze")).Status);

        var (status, abandoned) = await ba.PostAsync($"/api/ai-runs/{Id(run)}/abandon");
        Assert.Equal(200, status);
        Assert.Equal("Abandoned", Status(abandoned!));
        Assert.Equal(204, (await ba.GetAsync($"/api/requirement-sources/{sourceId}/ai-runs?operation=analyze")).Status);
    }

    [Fact]
    public async Task Migration_creates_the_run_and_fingerprint_tables_with_their_guards()
    {
        await using var host = new TestHost(chatClient: BuildStack().Client);

        Assert.Equal(1, await host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_AiChunkRuns_RequirementSourceId_Operation' AND is_unique = 1 AND filter_definition = '([Status] IN ((0), (1)))'"));
        Assert.Equal(1, await host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_AiChunkRunParts_AiChunkRunId_PartNumber' AND is_unique = 1"));
        Assert.Equal(1, await host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_ArtifactFingerprints_RequirementSourceId_ArtifactType_Fingerprint' AND is_unique = 1"));
        Assert.Equal(2, await host.ScalarAsync(
            "SELECT COUNT(*) FROM sys.columns WHERE name = 'RowVersion' AND object_id IN (OBJECT_ID('AiChunkRuns'), OBJECT_ID('AiChunkRunParts')) AND system_type_id = 189"));
        // Existing tables untouched: Artifacts gained no column.
        Assert.Equal(0, await host.ScalarAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('Artifacts') AND name LIKE '%Fingerprint%'"));
    }

    // Stands in for SqlException: ArtifactPersistence reads the error Number by reflection.
    private sealed class FakeSqlError(int number) : Exception("fake SQL error")
    {
        public int Number { get; } = number;
    }

    [Fact]
    public void Save_races_retried_are_unique_violations_and_deadlocks_including_ef_wrapped_ones()
    {
        static Microsoft.EntityFrameworkCore.DbUpdateException Update(int number) => new("save failed", new FakeSqlError(number));

        Assert.True(ArtifactPersistence.IsRetryableSaveRace(Update(2601)));
        Assert.True(ArtifactPersistence.IsRetryableSaveRace(Update(2627)));
        Assert.True(ArtifactPersistence.IsRetryableSaveRace(Update(1205)));
        // How EF's SQL Server execution strategy surfaced the deadlock seen in a full-suite run.
        Assert.True(ArtifactPersistence.IsRetryableSaveRace(new InvalidOperationException("transient failure", Update(1205))));

        Assert.False(ArtifactPersistence.IsRetryableSaveRace(Update(547))); // foreign key / check: not a race
        Assert.False(ArtifactPersistence.IsRetryableSaveRace(new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("rowversion")));
        Assert.False(ArtifactPersistence.IsRetryableSaveRace(new InvalidOperationException("other")));
    }

    [Fact]
    public void Fingerprints_normalize_each_field_and_keep_fields_apart()
    {
        Assert.Equal(
            ArtifactFingerprints.ForFunctional("The Employees", "Request Leave.", null, null, null, null),
            ArtifactFingerprints.ForFunctional("employee", "request leave", "", "", "", ""));
        Assert.NotEqual(
            ArtifactFingerprints.ForFunctional("Employee", "Request leave", "a", null, null, null),
            ArtifactFingerprints.ForFunctional("Employee", "Request leave", null, "a", null, null));
        Assert.NotEqual(
            ArtifactFingerprints.ForNonFunctional("Security", "Audit log", null, null),
            ArtifactFingerprints.ForNonFunctional("Compliance", "Audit log", null, null));
        Assert.Equal(64, ArtifactFingerprints.ForClarification("Who approves?").Length);
    }

    [Fact]
    public void Source_ids_and_section_anchors_are_found_deterministically()
    {
        Assert.Equal(["REQ-1.1", "FR-012", "TA-FR-01"], SrsIdExtractor.Extract("REQ-1.1 then FR-012, TA-FR-01 and req-1.1 again (REQ-1.1)."));
        var sections = SrsIdExtractor.SectionsById(SmallSrs);
        Assert.Equal("## 1. Leave requests", sections["REQ-1.1"]);
        Assert.Equal("## 2. Approval", sections["REQ-2.1"]);
        Assert.Equal(["## 1. A", "## 1. A", "## 2. B"], SourceChunker.SectionAnchors(["## 1. A\n\nText", "More text", "## 2. B\n\nText"]));
    }
}
