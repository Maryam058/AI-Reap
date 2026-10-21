using System.Text;
using System.Text.RegularExpressions;
using AiReap.Application.Ai;
using AiReap.Application.Knowledge;
using AiReap.Application.Traceability;
using Xunit;

namespace AiReap.Tests;

// The Project Copilot's deterministic context fitting (CopilotContextFitter): standard limits, the fit
// to a provider's input budget, and a report that matches the prompt. Pure - no database, no AI call.
// The provider route around it is covered in LosslessStageBudgetTests.
public class CopilotContextFitterTests
{
    public const string Question = "Which leave rules mention carry-over? QSENTINEL-7781 \"quoted\" and\nsecond line";
    private const string System = CopilotService.SystemPrompt;

    // ---- synthetic input -----------------------------------------------------------------------

    // Artifacts FR-001.. in relevance order, each with distinct content of contentChars characters.
    public static CopilotPromptInput Input(
        int artifacts = 60, int contentChars = 900, int versions = 15, int totalVersions = 40, int excerpts = 5,
        bool authorized = true, IReadOnlyList<CopilotImpactGroup>? impact = null, string question = Question, string? facts = null)
    {
        var items = Enumerable.Range(1, artifacts).Select(i => new CopilotArtifactItem(
            $"FR-{i:D3}", $"FR-{i:D3} [FunctionalRequirement, Approved] Leave rule {i}", Text($"rule{i}", contentChars))).ToList();
        var history = Enumerable.Range(1, Math.Min(versions, totalVersions))
            .Select(i => $"FR-{i:D3} v2 (2026-10-{i % 28 + 1:D2} 10:00:00Z, HumanEdit): clarified rule {i}").ToList();
        var chunks = authorized
            ? Enumerable.Range(0, excerpts).Select(i => new RetrievedChunk($"policy-{i}.pdf", i, Text($"excerpt{i}", 400), 0.9f - i * 0.1f)).ToList()
            : [];
        return new CopilotPromptInput(
            question,
            facts ?? $"Total artifacts: {artifacts}\nFunctional requirements with no linked test case (0): \n",
            items, impact, history, totalVersions, authorized, chunks,
            authorized ? (excerpts == 0 ? "(no project documents uploaded yet)" : "")
                       : "(project documents are not available to this user; answer from the artifacts above)");
    }

    // Distinct words, no newline, exactly `chars` characters.
    public static string Text(string label, int chars)
    {
        var sb = new StringBuilder();
        for (var i = 0; sb.Length < chars; i++)
        {
            sb.Append($"{label} clause {i} adds one more distinct leave detail; ");
        }

        return sb.ToString(0, chars);
    }

    public static IReadOnlyList<CopilotImpactGroup> Impact(int approved, int other) =>
    [
        new("FR-001", approved,
            Enumerable.Range(1, approved).Select(i => new CopilotImpactItem($"TC-{i:D3} (TestCase, Approved): Approved test {i} {Text($"a{i}", 120)}", true))
                .Concat(Enumerable.Range(1, other).Select(i => new CopilotImpactItem($"US-{i:D3} (UserStory, Draft): Draft story {i} {Text($"d{i}", 120)}", false)))
                .ToList())
    ];

    // ---- reading a prompt back -----------------------------------------------------------------

    public sealed record PromptCounts(int Full, int Truncated, int TitleOnly, int Shown, int Versions, int ImpactLines, int Excerpts);

    private static string Section(string prompt, string header, params string[] next)
    {
        var start = prompt.IndexOf(header + "\n", StringComparison.Ordinal);
        if (start < 0) return "";
        start += header.Length + 1;
        var end = next.Select(n => prompt.IndexOf("\n\n" + n + "\n", start, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(prompt.Length).Min();
        return prompt[start..end];
    }

    // What the prompt actually carries, counted from its text.
    public static PromptCounts Count(string prompt)
    {
        var artifacts = Section(prompt, "PROJECT ARTIFACTS:", "IMPACT ANALYSIS:", "RECENT CHANGES:")
            .Split('\n').Where(l => Regex.IsMatch(l, @"^FR-\d{3} \[")).ToList();
        var truncated = artifacts.Count(l => l.Contains(CopilotContextFitter.ShortenedMarker));
        var titleOnly = artifacts.Count(l => l.EndsWith(CopilotContextFitter.TitleOnlyMarker));
        var versions = Section(prompt, "RECENT CHANGES:", "DOCUMENT EXCERPTS:").Split('\n').Count(l => Regex.IsMatch(l, @"^FR-\d{3} v\d"));
        var impact = Section(prompt, "IMPACT ANALYSIS:", "RECENT CHANGES:").Split('\n').Count(l => l.StartsWith("- "));
        var excerpts = Regex.Matches(Section(prompt, "DOCUMENT EXCERPTS:", "CONTEXT NOTE:", "QUESTION:"), @"^\[policy-\d+\.pdf#\d+\]: ", RegexOptions.Multiline).Count;
        return new PromptCounts(artifacts.Count - truncated - titleOnly, truncated, titleOnly, artifacts.Count, versions, impact, excerpts);
    }

    // The report describes exactly the prompt: every count is checked against the prompt's text.
    public static void AssertReportMatchesPrompt(CopilotPromptInput input, CopilotFit fit)
    {
        var c = Count(fit.UserPrompt);
        var r = fit.Report;
        Assert.Equal(c.Full, r.ArtifactsFull);
        Assert.Equal(c.Truncated, r.ArtifactsTruncated);
        Assert.Equal(c.TitleOnly, r.ArtifactsTitleOnly);
        Assert.Equal(input.Artifacts.Count - c.Shown, r.ArtifactsOmitted);
        Assert.Equal(input.Artifacts.Count, r.ArtifactsFull + r.ArtifactsTruncated + r.ArtifactsTitleOnly + r.ArtifactsOmitted);
        Assert.Equal(input.TotalVersions - c.Versions, r.VersionsOmitted);
        Assert.Equal((input.Impact ?? []).Sum(g => g.Items.Count) - c.ImpactLines, r.ImpactCodesOmitted);
        if (input.ExcerptsAuthorized)
        {
            Assert.Equal(input.Excerpts.Count - c.Excerpts, r.ExcerptsOmitted);
            Assert.Equal(c.Excerpts, fit.SentExcerpts.Count);
        }
        else
        {
            Assert.Null(r.ExcerptsOmitted);
            Assert.Equal(0, c.Excerpts);
            Assert.Empty(fit.SentExcerpts);
        }

        var reduced = r.ArtifactsTruncated + r.ArtifactsTitleOnly + r.ArtifactsOmitted + r.VersionsOmitted + r.ImpactCodesOmitted
                      + (r.ExcerptsOmitted ?? 0) > 0;
        Assert.Equal(reduced, r.Reduced);
        Assert.Equal(reduced, fit.UserPrompt.Contains("\n\nCONTEXT NOTE:\n", StringComparison.Ordinal));
        Assert.Equal(PromptBudgetReducer.EstimateRequestTokens(System, fit.UserPrompt), fit.EstimatedTokens);

        // Full means complete content; a shortened artifact shows exactly its first 600 characters.
        var lines = fit.UserPrompt.Split('\n');
        foreach (var artifact in input.Artifacts)
        {
            var line = lines.FirstOrDefault(l => l.StartsWith(artifact.Header, StringComparison.Ordinal));
            if (line is null) continue;
            if (line.Contains(CopilotContextFitter.ShortenedMarker))
            {
                Assert.Equal($"{artifact.Header}{CopilotContextFitter.ShortenedMarker}{artifact.Content[..CopilotContextFitter.TruncatedContentChars]}…", line);
            }
            else if (!line.EndsWith(CopilotContextFitter.TitleOnlyMarker))
            {
                Assert.Equal($"{artifact.Header}: {artifact.Content}", line);
            }
        }
    }

    // The question is the last block, verbatim, and appears nowhere else.
    public static void AssertQuestionVerbatimOnce(string prompt, string question)
    {
        Assert.EndsWith("\n\nQUESTION:\n" + question, prompt, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Matches(prompt, Regex.Escape(question)).Count);
    }

    // ---- standard limits -----------------------------------------------------------------------

    [Fact]
    public void Small_project_is_sent_complete_with_no_reduction_and_no_context_note()
    {
        var input = Input(artifacts: 8, contentChars: 300, versions: 3, totalVersions: 3, excerpts: 2);
        var fit = CopilotContextFitter.StandardFit(System, input);

        Assert.False(fit.Report.Reduced);
        Assert.Null(fit.Report.Reason);
        Assert.Null(fit.Report.Provider);
        Assert.Equal(8, fit.Report.ArtifactsFull);
        Assert.Equal(0, fit.Report.ExcerptsOmitted);
        Assert.DoesNotContain("CONTEXT NOTE", fit.UserPrompt);
        Assert.All(input.Artifacts, a => Assert.Contains($"{a.Header}: {a.Content}\n", fit.UserPrompt));
        Assert.All(input.Excerpts, e => Assert.Contains($"[{e.Reference}]: {e.ChunkText}", fit.UserPrompt));
        AssertQuestionVerbatimOnce(fit.UserPrompt, Question);
        AssertReportMatchesPrompt(input, fit);
    }

    [Fact]
    public void Standard_limits_are_reported_even_without_a_provider_budget()
    {
        var input = Input(artifacts: 60, contentChars: 900);
        var fit = CopilotContextFitter.StandardFit(System, input);

        // 24,000 content characters: 26 complete 900-character artifacts, then one shortened to 600.
        Assert.Equal(26, fit.Report.ArtifactsFull);
        Assert.Equal(1, fit.Report.ArtifactsTruncated);
        Assert.Equal(33, fit.Report.ArtifactsTitleOnly);
        Assert.Equal(0, fit.Report.ArtifactsOmitted);
        Assert.Equal(25, fit.Report.VersionsOmitted); // 40 edits, the newest 15 listed
        Assert.True(fit.Report.Reduced);
        Assert.Equal(CopilotContextReport.StandardLimits, fit.Report.Reason);
        Assert.Null(fit.Report.Provider);
        Assert.Null(fit.Report.InputBudget);
        Assert.Contains("by the Copilot's standard context limits", fit.UserPrompt);
        Assert.Contains("never conclude that something does not exist", fit.UserPrompt);
        AssertQuestionVerbatimOnce(fit.UserPrompt, Question);
        AssertReportMatchesPrompt(input, fit);
    }

    [Fact]
    public void Standard_artifact_block_cap_leaves_later_artifacts_out_and_counts_them()
    {
        var input = Input(artifacts: 700, contentChars: 50, versions: 0, totalVersions: 0, excerpts: 0);
        var fit = CopilotContextFitter.StandardFit(System, input);

        Assert.True(fit.Report.ArtifactsOmitted > 0);
        Assert.Equal(40, fit.Report.ArtifactsFull);
        Assert.True(Section(fit.UserPrompt, "PROJECT ARTIFACTS:", "RECENT CHANGES:").Length <= CopilotContextFitter.StandardMaxArtifactBlockChars);
        AssertReportMatchesPrompt(input, fit);
    }

    // ---- provider budget -----------------------------------------------------------------------

    [Fact]
    public void Large_context_is_fitted_within_the_budget_less_the_repair_reserve()
    {
        var input = Input(artifacts: 60, contentChars: 900, impact: Impact(4, 6));
        var fit = CopilotContextFitter.FitToBudget(System, input, "Groq", 4000);

        Assert.True(fit.Fits);
        Assert.True(fit.EstimatedTokens <= 4000 - CopilotContextFitter.RepairReserveTokens, $"{fit.EstimatedTokens}");
        Assert.Equal(CopilotContextReport.ProviderBudget, fit.Report.Reason);
        Assert.Equal("Groq", fit.Report.Provider);
        Assert.Equal(4000, fit.Report.InputBudget);
        Assert.Contains("to fit the request budget of the AI provider serving this request", fit.UserPrompt);
        Assert.DoesNotContain("omitted to fit the AI provider's request budget", fit.UserPrompt); // PromptBudgetReducer's marker
        AssertQuestionVerbatimOnce(fit.UserPrompt, Question);
        AssertReportMatchesPrompt(input, fit);
    }

    [Fact]
    public void Identical_input_gives_a_byte_identical_prompt_and_report()
    {
        var first = CopilotContextFitter.FitToBudget(System, Input(impact: Impact(3, 3)), "Groq", 4000);
        var second = CopilotContextFitter.FitToBudget(System, Input(impact: Impact(3, 3)), "Groq", 4000);

        Assert.Equal(first.UserPrompt, second.UserPrompt);
        Assert.Equal(first.Report, second.Report);
        Assert.Equal(CopilotContextFitter.StandardFit(System, Input()).UserPrompt, CopilotContextFitter.StandardFit(System, Input()).UserPrompt);
    }

    [Theory]
    [InlineData(2_500)]
    [InlineData(3_000)]
    [InlineData(4_000)]
    [InlineData(6_000)]
    public void More_relevant_artifacts_are_never_reduced_more_than_less_relevant_ones(int budget)
    {
        var input = Input(artifacts: 60, contentChars: 900);
        var fit = CopilotContextFitter.FitToBudget(System, input, "Groq", budget);

        // Level per artifact in relevance order: 0 full, 1 shortened, 2 title only, 3 not shown.
        var lines = fit.UserPrompt.Split('\n');
        var levels = input.Artifacts.Select(a =>
        {
            var line = lines.FirstOrDefault(l => l.StartsWith(a.Header, StringComparison.Ordinal));
            return line is null ? 3
                : line.Contains(CopilotContextFitter.ShortenedMarker) ? 1
                : line.EndsWith(CopilotContextFitter.TitleOnlyMarker) ? 2 : 0;
        }).ToList();

        for (var i = 1; i < levels.Count; i++)
        {
            Assert.True(levels[i - 1] <= levels[i], $"budget {budget}: FR-{i:D3} level {levels[i - 1]} > FR-{i + 1:D3} level {levels[i]}");
        }

        // At Groq's configured budget and above, the most relevant artifact keeps its complete content
        // (below that, step 10 may shorten even the top 5).
        if (budget >= 4_000) Assert.Equal(0, levels[0]);
        AssertReportMatchesPrompt(input, fit);
    }

    [Fact]
    public void Excerpts_are_shown_whole_or_not_at_all_best_ranked_first()
    {
        var input = Input(artifacts: 30, contentChars: 900, excerpts: 5);
        for (var budget = 1_800; budget <= 9_000; budget += 150)
        {
            var fit = CopilotContextFitter.FitToBudget(System, input, "Groq", budget);
            var shown = input.Excerpts.Select(e => fit.UserPrompt.Contains($"[{e.Reference}]:", StringComparison.Ordinal)).ToList();
            for (var i = 0; i < input.Excerpts.Count; i++)
            {
                if (shown[i])
                {
                    Assert.Contains($"[{input.Excerpts[i].Reference}]: {input.Excerpts[i].ChunkText}", fit.UserPrompt);
                }

                // A lower-ranked excerpt is never shown while a better one is left out.
                if (i > 0 && shown[i]) Assert.True(shown[i - 1], $"budget {budget}: excerpt {i} shown without {i - 1}");
            }

            Assert.Equal(shown.Where(s => s).Count(), fit.SentExcerpts.Count);
            AssertReportMatchesPrompt(input, fit);
        }
    }

    [Fact]
    public void Impact_items_not_yet_approved_go_first_and_the_omissions_are_counted()
    {
        var input = Input(artifacts: 20, contentChars: 300, versions: 0, totalVersions: 0, excerpts: 0, impact: Impact(5, 8));
        var sawPartial = false;
        for (var budget = 1_000; budget <= 6_000; budget += 50)
        {
            var fit = CopilotContextFitter.FitToBudget(System, input, "Groq", budget);
            var approvedShown = input.Impact![0].Items.Where(i => i.ApprovedOrLater).Count(i => fit.UserPrompt.Contains("- " + i.Line));
            var otherShown = input.Impact[0].Items.Where(i => !i.ApprovedOrLater).Count(i => fit.UserPrompt.Contains("- " + i.Line));

            // An approved item is only dropped once every not-yet-approved item is.
            if (approvedShown < 5) Assert.Equal(0, otherShown);
            Assert.Equal(13 - approvedShown - otherShown, fit.Report.ImpactCodesOmitted);

            // The summary line keeps the total and approved counts the caller can see.
            Assert.Contains("If FR-001 changes, 13 downstream artifact(s) are potentially affected (5 already approved)", fit.UserPrompt);
            if (fit.Report.ImpactCodesOmitted > 0)
            {
                sawPartial = true;
                Assert.Contains($"; {13 - fit.Report.ImpactCodesOmitted} listed below, {fit.Report.ImpactCodesOmitted} not listed", fit.UserPrompt);
            }

            AssertReportMatchesPrompt(input, fit);
        }

        Assert.True(sawPartial);
    }

    [Fact]
    public void Required_content_that_cannot_fit_is_reported_as_not_fitting_with_the_question_intact()
    {
        var question = new string('q', 1_000) + " QSENTINEL";
        var input = Input(question: question, facts: Text("fact", 12_000));
        var fit = CopilotContextFitter.FitToBudget(System, input, "Groq", 4000);

        Assert.False(fit.Fits);
        Assert.True(fit.EstimatedTokens > 4000);
        // Everything optional was left out; the question and facts are untouched.
        Assert.Equal(0, fit.Report.ArtifactsFull + fit.Report.ArtifactsTruncated + fit.Report.ArtifactsTitleOnly);
        Assert.Equal(5, fit.Report.ExcerptsOmitted);
        Assert.Contains(input.Facts, fit.UserPrompt);
        AssertQuestionVerbatimOnce(fit.UserPrompt, question);
        AssertReportMatchesPrompt(input, fit);
    }

    [Fact]
    public void The_repair_reserve_is_given_up_before_the_request_is_refused()
    {
        // Required parts alone land between budget - reserve and budget: the minimal prompt is used.
        var baseline = CopilotContextFitter.FitToBudget(System, Input(artifacts: 1, contentChars: 10, excerpts: 0, totalVersions: 0, versions: 0), "Groq", 100_000);
        var budget = baseline.EstimatedTokens + CopilotContextFitter.RepairReserveTokens / 2;
        var input = Input(artifacts: 1, contentChars: 10, excerpts: 0, totalVersions: 0, versions: 0);
        var fit = CopilotContextFitter.FitToBudget(System, input, "Groq", budget);

        Assert.True(fit.Fits);
        Assert.True(fit.EstimatedTokens <= budget);
        AssertQuestionVerbatimOnce(fit.UserPrompt, Question);
    }

    // ---- privacy -------------------------------------------------------------------------------

    [Fact]
    public void A_caller_without_raw_source_access_gets_no_excerpts_and_no_document_counts()
    {
        var input = Input(authorized: false);
        foreach (var fit in new[] { CopilotContextFitter.StandardFit(System, input), CopilotContextFitter.FitToBudget(System, input, "Groq", 3000) })
        {
            Assert.Null(fit.Report.ExcerptsOmitted);
            Assert.Empty(fit.SentExcerpts);
            Assert.DoesNotContain("policy-", fit.UserPrompt);
            Assert.Contains("(project documents are not available to this user; answer from the artifacts above)", fit.UserPrompt);
            Assert.DoesNotContain("Document excerpts not shown", fit.UserPrompt);
            AssertReportMatchesPrompt(input, fit);
        }
    }

    [Fact]
    public void An_impact_path_through_an_artifact_the_caller_cannot_see_is_left_out()
    {
        var visible = new HashSet<string>(["FR-001", "US-002", "TC-003"], StringComparer.OrdinalIgnoreCase);
        var throughVisible = new ImpactedArtifact(Guid.NewGuid(), "TC-003", "Login test", "TestCase", "Approved", "TestedBy",
            "TC-003 tests US-002 implements FR-001");
        var throughHidden = throughVisible with { Path = "TC-003 tests US-009 implements FR-001" };

        Assert.Equal("TC-003 (TestCase, Approved): Login test [path: TC-003 tests US-002 implements FR-001]",
            CopilotService.ImpactItemLine(throughVisible, visible));
        var line = CopilotService.ImpactItemLine(throughHidden, visible);
        Assert.Equal("TC-003 (TestCase, Approved): Login test", line);
        Assert.DoesNotContain("US-009", line);
    }

    // ---- question length -----------------------------------------------------------------------

    [Fact]
    public void Question_limit_accepts_exactly_the_maximum_and_rejects_one_more_without_echoing_it()
    {
        Assert.Equal(2_000, CopilotQuestion.DefaultMaxQuestionChars);
        Assert.Null(CopilotQuestion.TooLong(new string('q', 2_000), 2_000));
        var message = CopilotQuestion.TooLong(new string('q', 2_001), 2_000);
        Assert.Equal("The question is 2,001 characters long; the Copilot accepts at most 2,000. Shorten the question and ask again - " +
                     "it is never shortened automatically.", message);
        Assert.DoesNotContain("qq", message);

        Assert.Equal(2_000, CopilotQuestion.EffectiveMax(0));
        Assert.Equal(2_000, CopilotQuestion.EffectiveMax(-5));
        Assert.Equal(500, CopilotQuestion.EffectiveMax(500));
        Assert.Equal(2_000, new AiReap.Infrastructure.Ai.CopilotSettings(
            Microsoft.Extensions.Options.Options.Create(new AiReap.Infrastructure.Ai.CopilotOptions { MaxQuestionChars = 0 })).MaxQuestionChars);
        Assert.Equal(2_000, new AiReap.Infrastructure.Ai.CopilotSettings(
            Microsoft.Extensions.Options.Options.Create(new AiReap.Infrastructure.Ai.CopilotOptions())).MaxQuestionChars);
    }
}
