using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;
using AiReap.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiReap.Tests;

// Groq's free tier rejects any single request above its tokens-per-minute limit (413 "Request too
// large ... Limit 8000, Requested 14410"). Gemini stays primary and untouched; requests that fall
// back to Groq are condensed by RequestBudgetAiChatClient/PromptBudgetReducer to fit
// Ai:Groq:MaxRequestTokens. Real Gemini/Groq clients over fake HTTP - never a live API.
public class GroqRequestBudgetTests
{
    private const string GeminiKey = "AIzaSyTESTTESTTESTTESTTESTTESTTESTTEST12";
    private const string GroqKey = "gsk_TESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTESTTEST";
    private const int MaxRequestTokens = 8000;
    private const int ReservedOutputTokens = 4000;
    private const int InputBudget = MaxRequestTokens - ReservedOutputTokens;

    private const string SystemPrompt = """
        You are a requirements analyst. Respond with ONLY a single JSON object matching exactly this shape:
        {"actors": ["string"], "capabilities": ["string"], "notes": "string"}
        """;

    // Requirement statements buried in a long document; each must survive condensing.
    private static readonly string[] KeyRequirements =
    [
        "Managers must approve or reject each leave request within 3 working days.",
        "Employees shall not request more than 25 days of annual leave per calendar year.",
        "Only HR administrators can change an employee's leave balance, and every change must be audited.",
        "The system must notify the employee by email when a request is approved or rejected."
    ];

    private const string PageHeader = "ACME Corporation - Internal - Confidential";

    // ~60k characters (~15k tokens, like the 68.5k-character source that failed): narrative filler
    // with no requirement content, a page header repeated every ten paragraphs, and the key
    // requirements spread through it.
    private static string LargeSource()
    {
        var sb = new StringBuilder("Leave Management System - Requirements\n\n");
        sb.Append("This document describes the leave management system the company needs. It must replace spreadsheets.\n\n");
        for (var i = 0; i < 160; i++)
        {
            if (i % 10 == 0) sb.Append(PageHeader).Append("\n\n");
            if (i % 40 == 20) sb.Append(KeyRequirements[i / 40]).Append("\n\n");
            sb.Append($"Narrative {Letters(i)}: the organisation has grown steadily through several acquisitions and the ")
              .Append("teams are spread across many offices in different regions, each with its own history, culture and ")
              .Append("habits, which this background paragraph describes in general terms for the interested reader.\n\n");
        }

        sb.Append($"Narrative {Letters(999)}: closing remarks about the history of the organisation.");
        return sb.ToString();
    }

    // Letter-only ids, so the filler carries no numbers (numbers count as a requirement signal).
    private static string Letters(int n)
    {
        var s = "";
        do { s = (char)('a' + n % 26) + s; n /= 26; } while (n > 0);
        return "x" + s;
    }

    private const string AnalysisJson = "{\"actors\":[\"Manager\",\"Employee\"],\"capabilities\":[\"Approve leave\"],\"notes\":\"from groq\"}";

    private static HttpResponseMessage GroqOk(string content) => GroqAiChatClientTests.Json(GroqAiChatClientTests.Success(content));

    private static HttpResponseMessage GeminiQuota() => new(HttpStatusCode.TooManyRequests)
    {
        Content = JsonContent.Create(new { error = new { code = 429, message = "You exceeded your current quota (per day).", status = "RESOURCE_EXHAUSTED" } })
    };

    private static HttpResponseMessage GeminiOk(string text) => GroqAiChatClientTests.Json(new
    {
        candidates = new[] { new { content = new { role = "model", parts = new[] { new { text } } }, finishReason = "STOP" } }
    });

    // Wired exactly as DependencyInjection wires it: Gemini resilient (no 429 retry), Groq
    // resilient inside the request budget, both inside FallbackAiChatClient.
    internal static (FallbackAiChatClient Client, GroqAiChatClientTests.FakeHandler GeminiHttp, GroqAiChatClientTests.FakeHandler GroqHttp) BuildStack(
        Func<HttpRequestMessage, HttpResponseMessage> geminiRespond, Func<HttpRequestMessage, HttpResponseMessage> groqRespond)
    {
        var geminiHttp = new GroqAiChatClientTests.FakeHandler(geminiRespond);
        var groqHttp = new GroqAiChatClientTests.FakeHandler(groqRespond);
        var gemini = new GeminiAiChatClient(new HttpClient(geminiHttp), Options.Create(new GeminiOptions { ApiKey = GeminiKey }));
        var groq = new GroqAiChatClient(new HttpClient(groqHttp), Options.Create(new GroqOptions { ApiKey = GroqKey }));
        var resilience = new AiResilienceOptions { BaseDelayMilliseconds = 0 };
        Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

        var client = new FallbackAiChatClient(
            new ResilientAiChatClient(gemini, "Gemini", resilience.WithoutRateLimitRetry(), NullLogger.Instance, NoDelay), "Gemini",
            new RequestBudgetAiChatClient(
                new ResilientAiChatClient(groq, "Groq", resilience, NullLogger.Instance, NoDelay), "Groq",
                "Ai:Groq:MaxRequestTokens", MaxRequestTokens, ReservedOutputTokens, NullLogger.Instance), "Groq",
            new AiProviderCooldown(), TimeSpan.FromMinutes(10), NullLogger.Instance);
        return (client, geminiHttp, groqHttp);
    }

    // What reaches Groq's HTTP endpoint: estimated input within the input budget, and estimated
    // input + max_completion_tokens within Ai:Groq:MaxRequestTokens.
    private static void AssertWithinGroqBudget(string body)
    {
        var (system, user) = GroqMessages(body);
        var input = PromptBudgetReducer.EstimateRequestTokens(system, user);
        var maxCompletion = JsonNode.Parse(body)!["max_completion_tokens"]!.GetValue<int>();
        Assert.True(input <= InputBudget, $"Groq input of ~{input} tokens exceeds the {InputBudget}-token input budget.");
        Assert.True(maxCompletion >= ReservedOutputTokens, $"max_completion_tokens {maxCompletion} is below the {ReservedOutputTokens} reserve.");
        Assert.True(input + maxCompletion <= MaxRequestTokens,
            $"Groq request of ~{input} input + {maxCompletion} max_completion_tokens exceeds MaxRequestTokens {MaxRequestTokens}.");
    }

    // A source whose Groq request (SystemPrompt + source + framing) is estimated at exactly
    // `requestTokens`, with the key requirements spread through it.
    private static string SourceForEstimatedRequest(int requestTokens)
    {
        var chars = (int)((requestTokens - PromptBudgetReducer.RequestOverheadTokens - PromptBudgetReducer.EstimateTokens(SystemPrompt))
            * PromptBudgetReducer.CharsPerToken);
        var sb = new StringBuilder("Leave Management System - Requirements\n\n");
        var keyAt = new Queue<int>(Enumerable.Range(1, KeyRequirements.Length).Select(k => chars * k / (KeyRequirements.Length + 2)));
        for (var i = 0; sb.Length < chars; i++)
        {
            if (i % 10 == 0) sb.Append(PageHeader).Append("\n\n");
            if (keyAt.Count > 0 && sb.Length >= keyAt.Peek())
            {
                sb.Append(KeyRequirements[KeyRequirements.Length - keyAt.Count]).Append("\n\n");
                keyAt.Dequeue();
            }

            sb.Append($"Narrative {Letters(i)}: the organisation has grown steadily through several acquisitions and the ")
              .Append("teams are spread across many offices in different regions, each with its own history, culture and ")
              .Append("habits, which this background paragraph describes in general terms for the interested reader.\n\n");
        }

        return sb.ToString(0, chars);
    }

    private static (string System, string User) GroqMessages(string body)
    {
        var messages = JsonNode.Parse(body)!["messages"]!.AsArray();
        return (messages[0]!["content"]!.GetValue<string>(), messages[1]!["content"]!.GetValue<string>());
    }

    // 1. Gemini primary: the request is sent exactly as built, however large.
    [Fact]
    public async Task Gemini_receives_the_full_unmodified_request_even_when_it_exceeds_the_groq_budget()
    {
        var source = LargeSource();
        var (client, geminiHttp, groqHttp) = BuildStack(_ => GeminiOk(AnalysisJson), _ => GroqOk(AnalysisJson));

        await client.CompleteAsync(SystemPrompt, source);

        var sentText = JsonNode.Parse(Assert.Single(geminiHttp.Bodies))!["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Equal(source, sentText);
        Assert.Empty(groqHttp.Requests);
        Assert.Equal("gemini-3.8-flash", client.ModelName);
    }

    // 2. Gemini quota -> Groq; a request inside the budget reaches Groq byte-for-byte.
    [Fact]
    public async Task Gemini_quota_falls_back_to_groq_and_a_request_within_budget_is_not_altered()
    {
        const string prompt = "Employees submit leave requests; managers approve them.";
        var (client, geminiHttp, groqHttp) = BuildStack(_ => GeminiQuota(), _ => GroqOk(AnalysisJson));

        Assert.Equal(AnalysisJson, await client.CompleteAsync(SystemPrompt, prompt));

        Assert.Single(geminiHttp.Requests);
        var (system, user) = GroqMessages(Assert.Single(groqHttp.Bodies));
        Assert.Equal(SystemPrompt, system);
        Assert.Equal(prompt, user);
        Assert.Equal("groq:openai/gpt-oss-120b", client.ModelName);
    }

    // 3, 4, 5, 8. A large request is condensed before Groq, stays inside the configured input
    // budget, keeps the system prompt/schema, every requirement statement and the context the
    // pipeline added, and the JSON response still parses.
    [Fact]
    public async Task Large_request_is_condensed_within_the_groq_budget_keeping_requirements_and_context()
    {
        var source = LargeSource();
        const string context = "\n\nFunctional requirements derived so far:\n- FR-001: Submit leave request\n- FR-002: Approve leave request\n";
        var original = source + context;
        Assert.True(PromptBudgetReducer.EstimateTokens(original) > MaxRequestTokens); // the 413 case

        var (client, _, groqHttp) = BuildStack(_ => GeminiQuota(), _ => GroqOk(AnalysisJson));

        var (_, parsed) = await GenerationSupport.CallAiAndParseAsync<GroqAiChatClientTests.AnalysisShape>(
            client, NullLogger.Instance, "RequirementAnalysis", "src-1", SystemPrompt, original, CancellationToken.None);

        Assert.Equal("from groq", parsed.Notes);
        var (system, user) = GroqMessages(Assert.Single(groqHttp.Bodies));
        Assert.Equal(SystemPrompt, system); // schema/instructions never condensed
        AssertWithinGroqBudget(Assert.Single(groqHttp.Bodies));
        Assert.True(user.Length < original.Length / 2);

        foreach (var requirement in KeyRequirements)
        {
            Assert.Contains(requirement, user);
        }

        Assert.Contains("Leave Management System - Requirements", user);
        Assert.Contains("- FR-001: Submit leave request\n- FR-002: Approve leave request", user);
        Assert.StartsWith("[AI-REAP note:", user);
        Assert.Contains("omitted to fit the AI provider's request budget", user);
        Assert.True(CountOf(user, "Narrative x") < 161 / 2); // most of the no-signal narrative went first
        Assert.Equal(1, CountOf(user, PageHeader)); // repeated page header kept once
    }

    [Fact]
    public void Prompt_within_budget_is_returned_unchanged()
    {
        const string prompt = "Users log in.\n\n\n\nManagers approve leave.   ";

        var fit = PromptBudgetReducer.Fit(SystemPrompt, prompt, InputBudget);

        Assert.True(fit.Fits);
        Assert.False(fit.WasReduced);
        Assert.Same(prompt, fit.UserPrompt);
    }

    [Fact]
    public void Lossless_cleanup_is_used_alone_when_it_is_enough()
    {
        // Mostly duplicated passages and whitespace: removing them fits without dropping content.
        var paragraph = "Managers must approve each leave request within 3 working days.";
        var prompt = string.Join("\n\n\n\n", Enumerable.Repeat(paragraph + "          ", 40)) + "\n\nEmployees shall submit requests online.";

        var fit = PromptBudgetReducer.Fit("s", prompt, 200);

        Assert.True(fit.Fits);
        Assert.True(fit.WasReduced);
        Assert.Equal(paragraph + "\n\nEmployees shall submit requests online.", fit.UserPrompt);
        Assert.Equal(0, fit.OmittedPassages);
    }

    // The repair attempt appends validation feedback; condensing must never drop it.
    [Fact]
    public void Repair_instruction_and_clarifications_survive_condensing()
    {
        const string clarifications = "\n\nClarifications gathered so far:\n- Q: Who approves leave?\n  A: The line manager.\n";
        const string repair = "\n\n---\nYour previous response was rejected by automatic validation for these reasons:\n- actors is empty\nReturn a corrected response.";

        var fit = PromptBudgetReducer.Fit(SystemPrompt, LargeSource() + clarifications + repair, InputBudget);

        Assert.True(fit.Fits);
        Assert.Contains("Clarifications gathered so far:\n- Q: Who approves leave?\n  A: The line manager.", fit.UserPrompt);
        Assert.EndsWith("---\nYour previous response was rejected by automatic validation for these reasons:\n- actors is empty\nReturn a corrected response.", fit.UserPrompt);
    }

    // Regression: each "[... omitted ...]" marker was planned at 16 tokens but costs ~25 (+1 for its
    // separator). In a structured document where almost every passage carries a requirement signal,
    // selection keeps and drops passages alternately, the per-marker shortfall added up, and the
    // rendered prompt overshot the budget - so a condensable request was refused ("needs about
    // 23,580"). The plan must now always produce a prompt that fits.
    [Fact]
    public async Task Structured_source_of_about_23580_tokens_is_condensed_instead_of_refused()
    {
        var source = ChunkedAnalysisTests.StructuredSource(23_580, SystemPrompt);
        Assert.Equal(23_580, PromptBudgetReducer.EstimateRequestTokens(SystemPrompt, source));

        var fit = PromptBudgetReducer.Fit(SystemPrompt, source, InputBudget);

        Assert.True(fit.Fits);
        Assert.True(fit.OmittedPassages > 0);
        var gaps = CountOf(fit.UserPrompt, "omitted to fit the AI provider's request budget");
        Assert.True(gaps > 1, $"expected several omission gaps, found {gaps}");
        Assert.Equal(PromptBudgetReducer.EstimateRequestTokens(SystemPrompt, fit.UserPrompt), fit.FinalTokens);
        Assert.True(fit.FinalTokens <= InputBudget);

        // End to end: one condensed Groq request within the budget, not a refusal.
        var (client, _, groqHttp) = BuildStack(_ => GeminiQuota(), _ => GroqOk(AnalysisJson));
        Assert.Equal(AnalysisJson, await client.CompleteAsync(SystemPrompt, source));
        AssertWithinGroqBudget(Assert.Single(groqHttp.Bodies));
    }

    // Across budgets and source shapes (alternating selections included), the planned selection
    // never renders over budget: a source with room for its fixed parts is always condensed.
    [Theory]
    [InlineData(12_000)]
    [InlineData(23_580)]
    [InlineData(40_000)]
    public void Condensed_prompt_never_exceeds_the_budget_because_of_omission_markers(int requestTokens)
    {
        var sources = new[] { ChunkedAnalysisTests.StructuredSource(requestTokens, SystemPrompt), SourceForEstimatedRequest(requestTokens) };
        foreach (var source in sources)
        {
            for (var budget = 1_000; budget <= InputBudget; budget += 250)
            {
                var fit = PromptBudgetReducer.Fit(SystemPrompt, source, budget);

                Assert.True(fit.Fits, $"~{requestTokens}-token source refused at budget {budget} (rendered ~{fit.FinalTokens}).");
                Assert.True(fit.FinalTokens <= budget);
                Assert.Equal(PromptBudgetReducer.EstimateRequestTokens(SystemPrompt, fit.UserPrompt), fit.FinalTokens);
            }
        }
    }

    // 6. Retrieved excerpts rank below the source text, and among themselves by retrieval rank.
    [Fact]
    public void Source_text_is_kept_before_rag_excerpts_and_the_best_ranked_excerpts_are_kept_first()
    {
        var source = string.Join("\n\n", KeyRequirements);
        var excerpts = string.Join("\n\n", Enumerable.Range(0, 4).Select(i =>
            $"[notes.pdf#{i}]: " + string.Concat(Enumerable.Repeat($"Excerpt {Letters(i)} text about leave approvals and policies. ", 50))));
        var prompt = source + "\n\nProject document excerpts (label in brackets):\n" + excerpts;

        // Room for the source and two of the four ~750-token excerpts.
        var fit = PromptBudgetReducer.Fit("s", prompt, 2400);

        Assert.True(fit.Fits);
        Assert.All(KeyRequirements, r => Assert.Contains(r, fit.UserPrompt));
        Assert.Contains("[notes.pdf#0]:", fit.UserPrompt);
        Assert.Contains("[notes.pdf#1]:", fit.UserPrompt);
        Assert.DoesNotContain("[notes.pdf#3]:", fit.UserPrompt);
        Assert.True(PromptBudgetReducer.EstimateTokens("s" + fit.UserPrompt) <= 2400);
    }

    // 7a. Cannot fit even after condensing (the must-keep context alone is too big): refused
    // before any Groq call, with a clear message, and never retried or repaired.
    [Fact]
    public async Task Source_that_cannot_fit_is_refused_without_calling_groq()
    {
        var hugeContext = "\n\nFunctional requirements derived so far:\n" +
            string.Concat(Enumerable.Range(1, 2000).Select(i => $"- FR-{i:000}: Requirement title number {i}\n"));
        var (client, _, groqHttp) = BuildStack(_ => GeminiQuota(), _ => GroqOk(AnalysisJson));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => GenerationSupport.CallAiAndParseAsync<GroqAiChatClientTests.AnalysisShape>(
            client, NullLogger.Instance, "ImplementationPlanning", "src-1", SystemPrompt, LargeSource() + hugeContext, CancellationToken.None));

        Assert.Equal(AiFailureKind.RequestRejected, ex.Kind);
        Assert.Contains("too large for the configured Groq request budget", ex.ProviderMessage);
        Assert.Contains("Ai:Groq:MaxRequestTokens=8000", ex.ProviderMessage);
        Assert.Empty(groqHttp.Requests);
    }

    // 7b. Groq still says 413 (its tokenizer disagrees with the estimate): exactly one request,
    // no retry, no repair attempt, and a clean message without Groq's organization details.
    [Fact]
    public async Task Groq_413_is_reported_clearly_and_sent_only_once()
    {
        var (client, _, groqHttp) = BuildStack(_ => GeminiQuota(), _ => GroqAiChatClientTests.Json(new
        {
            error = new
            {
                message = "Request too large for model `openai/gpt-oss-120b` in organization `org_01testtesttest` service tier `on_demand` on tokens per minute (TPM): Limit 8000, Requested 14410, please reduce your message size and try again.",
                type = "tokens",
                code = "rate_limit_exceeded"
            }
        }, HttpStatusCode.RequestEntityTooLarge));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => GenerationSupport.CallAiAndParseAsync<GroqAiChatClientTests.AnalysisShape>(
            client, NullLogger.Instance, "RequirementAnalysis", "src-1", SystemPrompt, LargeSource(), CancellationToken.None));

        Assert.Single(groqHttp.Requests);
        Assert.Equal(AiFailureKind.RequestRejected, ex.Kind);
        Assert.Contains("too large for the configured Groq request budget", ex.ProviderMessage);
        Assert.DoesNotContain("org_", ex.ProviderMessage);
        Assert.DoesNotContain("org_", ex.Message);
    }

    // The reported failure: Gemini quota exhausted, generate-tasks fell back to Groq with a request
    // Groq counted as "Limit 8000, Requested 15572". The same-sized request is condensed before the
    // Groq HTTP call; the full-size prompt never reaches Groq.
    [Fact]
    public async Task Request_of_about_15572_tokens_is_condensed_before_the_groq_http_call()
    {
        var source = SourceForEstimatedRequest(15_572);
        Assert.Equal(15_572, PromptBudgetReducer.EstimateRequestTokens(SystemPrompt, source));
        var (client, geminiHttp, groqHttp) = BuildStack(_ => GeminiQuota(), _ => GroqOk(AnalysisJson));

        Assert.Equal(AnalysisJson, await client.CompleteAsync(SystemPrompt, source));

        Assert.Single(geminiHttp.Requests);
        var body = Assert.Single(groqHttp.Bodies);
        AssertWithinGroqBudget(body);
        var (_, user) = GroqMessages(body);
        Assert.NotEqual(source, user);
        Assert.All(KeyRequirements, r => Assert.Contains(r, user));
        Assert.Equal("groq:openai/gpt-oss-120b", client.ModelName);
    }

    // The same ~15,572-token request when it is almost all must-keep context (nothing to condense):
    // refused before the Groq HTTP call, with the budget named in the message.
    [Fact]
    public async Task Request_of_about_15572_tokens_that_cannot_be_condensed_is_rejected_before_the_groq_http_call()
    {
        var context = "Functional requirements derived so far:\n" +
            string.Concat(Enumerable.Range(1, 1150).Select(i => $"- FR-{i:0000}: Requirement title number {i}\n"));
        var prompt = "Managers must approve leave.\n\n" + context;
        Assert.InRange(PromptBudgetReducer.EstimateRequestTokens(SystemPrompt, prompt), 15_000, 16_500);
        var (client, _, groqHttp) = BuildStack(_ => GeminiQuota(), _ => GroqOk(AnalysisJson));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => client.CompleteAsync(SystemPrompt, prompt));

        Assert.Empty(groqHttp.Requests);
        Assert.Equal(AiFailureKind.RequestRejected, ex.Kind);
        Assert.Contains("too large for the configured Groq request budget", ex.ProviderMessage);
        Assert.Contains("Ai:Groq:MaxRequestTokens=8000", ex.ProviderMessage);
    }

    // Defence in depth: GroqAiChatClient itself refuses an over-budget request even when nothing
    // condensed it first (e.g. a wiring that bypasses RequestBudgetAiChatClient).
    [Fact]
    public async Task Groq_client_refuses_an_oversized_request_without_an_http_call_even_when_unwrapped()
    {
        var (groq, handler) = GroqAiChatClientTests.Build(_ => GroqOk(AnalysisJson));

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => groq.CompleteAsync(SystemPrompt, SourceForEstimatedRequest(15_572)));

        Assert.Empty(handler.Requests);
        Assert.Equal(AiFailureKind.RequestRejected, ex.Kind);
        Assert.Contains("too large for the configured Groq request budget", ex.ProviderMessage);
        Assert.Contains("15,572", ex.ProviderMessage);
    }

    // Input inside the budget: sent, with max_completion_tokens lowered from the configured 16,384
    // so estimated input + output allowance stays within MaxRequestTokens.
    [Theory]
    [InlineData(500)]
    [InlineData(3_000)]
    [InlineData(InputBudget)]
    public async Task Groq_client_caps_max_completion_tokens_to_the_rest_of_the_request_budget(int requestTokens)
    {
        var handler = new GroqAiChatClientTests.FakeHandler(_ => GroqOk(AnalysisJson));
        var groq = new GroqAiChatClient(new HttpClient(handler), Options.Create(new GroqOptions
        {
            ApiKey = GroqKey, MaxCompletionTokens = 16_384, MaxRequestTokens = MaxRequestTokens, ReservedOutputTokens = ReservedOutputTokens
        }));

        await groq.CompleteAsync(SystemPrompt, SourceForEstimatedRequest(requestTokens));

        var body = Assert.Single(handler.Bodies);
        AssertWithinGroqBudget(body);
        Assert.Equal(MaxRequestTokens - requestTokens, JsonNode.Parse(body)!["max_completion_tokens"]!.GetValue<int>());
    }

    // 9. Through the real API: analysis served by Groq after a Gemini quota failure is split into
    // parts that each fit the budget (never condensed - ChunkedAnalysisTests), parsed, merged,
    // persisted, and audited with the Groq model.
    [Fact]
    public async Task Analysis_of_a_large_source_served_by_groq_is_audited_with_the_groq_model()
    {
        var (client, _, groqHttp) = BuildStack(_ => GeminiQuota(), _ => GroqOk(
            "{\"actors\":[\"Manager\"],\"capabilities\":[\"Approve leave\"],\"dataElements\":[],\"notes\":[]," +
            "\"missingInformation\":[{\"topic\":\"Carry-over\",\"question\":\"Can unused leave carry over?\",\"reason\":\"Not stated\"}]}"));
        await using var host = new TestHost(chatClient: client);
        var ba = await host.RegisterAsync("BusinessAnalyst");

        var (ps, project) = await ba.PostAsync("/api/projects", new { name = "Budget", description = "d" });
        Assert.Equal(201, ps);
        var projectId = project!["id"]!.GetValue<string>();
        var (ss, source) = await ba.PostAsync($"/api/projects/{projectId}/requirement-sources", new { sourceType = 1, rawText = LargeSource() });
        Assert.InRange(ss, 200, 201);

        var (analyzeStatus, analysis) = await ba.PostAsync($"/api/requirement-sources/{source!["id"]!.GetValue<string>()}/analyze");

        Assert.Equal(200, analyzeStatus);
        Assert.Equal("Manager", analysis!["actors"]![0]!.GetValue<string>());
        Assert.True(groqHttp.Bodies.Count > 1);
        Assert.All(groqHttp.Bodies, AssertWithinGroqBudget);
        var users = groqHttp.Bodies.Select(b => GroqMessages(b).User).ToList();
        Assert.All(KeyRequirements, r => Assert.Single(users, u => u.Contains(r)));

        var (auditStatus, audit) = await ba.GetAsync($"/api/projects/{projectId}/ai-executions");
        Assert.Equal(200, auditStatus);
        var row = audit!.AsArray().Single(e => e!["operationType"]!.GetValue<string>() == "RequirementAnalysis");
        Assert.Equal("groq:openai/gpt-oss-120b", row!["model"]!.GetValue<string>());
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
