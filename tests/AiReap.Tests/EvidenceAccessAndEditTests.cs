using System.Text.Json.Nodes;
using AiReap.Domain.Common;
using Xunit;
using static AiReap.Tests.GroundedGenerationTests;
using static AiReap.Tests.LifecycleTestSupport;

namespace AiReap.Tests;

// P0-2 — a generated requirement's evidence is server-owned (a human edit can neither forge nor keep a
// stale result) and, under S2, a Developer never receives quote text through any read path: the
// artifact list, a single artifact, its versions, the AI audit trail or the Copilot. They do get the
// statuses. Writers and reviewers see the quotes.
public class EvidenceAccessAndEditTests : IAsyncLifetime
{
    private const string SrsQuote = "Employees must be able to submit a leave request online";
    private const string AnswerQuote = "Approval can be delegated to a deputy manager";
    private const string InventedQuote = "Employees may cancel approved leave at any time";
    private const string TargetQuote = "The leave page must load within 3 seconds for 95% of requests";

    private readonly TestHost _host = new();
    private Actor _ba = null!, _reviewer = null!, _dev = null!;
    private string _projectId = null!;
    private JsonNode _generated = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.ActorAsync(Roles.BusinessAnalyst);
        _reviewer = await _host.ActorAsync(Roles.Reviewer);
        _dev = await _host.ActorAsync(Roles.Developer);
        string sourceId;
        (_projectId, sourceId) = await SourceAsync(_ba.Api);
        foreach (var member in new[] { _reviewer, _dev })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba.Api, _projectId, member.Api)).Status, 200, 201);
        }

        await AnalyzeAndAnswerAsync(_ba.Api, _projectId, sourceId);
        _host.Ai.RespondOnceWhenPromptContains("requirementOrigin", Response(
            [
                FrItem("Submit leave", "explicit", new JsonArray(Quote("srs", SrsQuote, "FR-001"), Quote("clarification", AnswerQuote, reference: "CQ-001"))),
                FrItem("Cancel leave", "explicit", new JsonArray(Quote("srs", InventedQuote)))
            ],
            [
                NfrItem("Page load", "3 seconds", "stated", new JsonArray(Quote("srs", TargetQuote, "NFR-001"))),
                NfrItem("Availability", "99.9%", "proposed", null)
            ]));
        var (status, generated) = await _ba.Api.PostAsync($"/api/requirement-sources/{sourceId}/generate-requirements");
        Assert.Equal(200, status);
        _generated = generated!;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private string IdOf(string title) =>
        Id(_generated["functionalRequirements"]!.AsArray().Concat(_generated["nonFunctionalRequirements"]!.AsArray())
            .Single(a => a!["title"]!.GetValue<string>() == title));

    private async Task<JsonObject> DataAsync(Actor user, string id) =>
        (await user.Api.GetAsync($"/api/artifacts/{id}")).Body!["data"]!.DeepClone().AsObject();

    private static Task<(int Status, JsonNode? Body)> EditAsync(Actor user, string id, JsonObject data, string reason = "edit") =>
        user.Api.SendAsync(HttpMethod.Patch, $"/api/artifacts/{id}", new { data, reason });

    private static string? Str(JsonNode? node, string key) => node?[key]?.GetValue<string>();

    // ---- edits -----------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_that_marks_evidence_verified_or_rewrites_a_quote_is_reverted()
    {
        var id = IdOf("Cancel leave");
        var data = await DataAsync(_ba, id);
        Assert.Equal("unverified", Str(data, "evidenceStatus"));

        data["evidenceStatus"] = "verified";
        data["evidence"]![0]!["status"] = "verified";
        data["evidence"]![0]!["reason"] = null;
        data["evidence"]![0]!["quote"] = SrsQuote;
        data["evidenceCheck"] = "trust-me";
        data["actor"] = "Staff member";
        var (status, edited) = await EditAsync(_ba, id, data);

        Assert.Equal(200, status);
        var after = edited!["data"]!;
        Assert.Equal("Staff member", Str(after, "actor"));
        Assert.Equal("unverified", Str(after, "evidenceStatus"));
        Assert.Equal(("unverified", InventedQuote), (Str(after["evidence"]![0], "status"), Str(after["evidence"]![0], "quote")));
        Assert.Equal("SourceEvidence-v1", Str(after, "evidenceCheck"));

        var nfrId = IdOf("Availability");
        var nfr = await DataAsync(_ba, nfrId);
        nfr["targetSupport"] = "quote_found";
        var (_, editedNfr) = await EditAsync(_ba, nfrId, nfr);
        Assert.Null(Str(editedNfr!["data"], "targetSupport"));
        Assert.Equal("proposed_assumption", Str(editedNfr["data"], "assumptionStatus"));
    }

    [Fact]
    public async Task An_edit_of_the_expected_result_recalculates_unsupported_numbers()
    {
        var id = IdOf("Submit leave");
        var data = await DataAsync(_ba, id);
        Assert.Empty(data["unsupportedNumbers"]!.AsArray());

        data["expectedResult"] = "Stored within 10 minutes";
        data["unsupportedNumbers"] = new JsonArray();
        var (_, edited) = await EditAsync(_ba, id, data);

        Assert.Equal(["10 minutes"], edited!["data"]!["unsupportedNumbers"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task An_edit_of_requirement_origin_is_kept_and_versioned()
    {
        var id = IdOf("Submit leave");
        var data = await DataAsync(_ba, id);
        data["requirementOrigin"] = "derived";
        data["derivationNote"] = "Reviewer: follows from FR-001 and the approval answer";
        var (status, edited) = await EditAsync(_ba, id, data, "reclassified");

        Assert.Equal(200, status);
        Assert.Equal("derived", Str(edited!["data"], "requirementOrigin"));
        Assert.Equal(2, edited["currentVersion"]!.GetValue<int>());
        Assert.Equal("verified", Str(edited["data"], "evidenceStatus")); // evidence untouched

        var versions = (await _ba.Api.GetAsync($"/api/artifacts/{id}/versions")).Body!.AsArray();
        Assert.Equal(["derived", "explicit"], versions.Select(v => Str(v!["data"], "requirementOrigin")));
        Assert.Equal("reclassified", Str(versions[0], "reason"));
    }

    // ---- S2 read paths ---------------------------------------------------------------------

    private static readonly string[] QuoteTexts = [SrsQuote, AnswerQuote, InventedQuote, TargetQuote];

    private static void AssertNoQuoteText(string what, string json)
    {
        foreach (var quote in QuoteTexts)
        {
            Assert.False(json.Contains(quote, StringComparison.OrdinalIgnoreCase), $"{what} contains quote text \"{quote}\".");
        }
    }

    private async Task ApproveAllAsync()
    {
        foreach (var title in new[] { "Submit leave", "Cancel leave", "Page load", "Availability" })
        {
            Assert.Equal(200, (await _reviewer.Api.SendAsync(HttpMethod.Patch, $"/api/artifacts/{IdOf(title)}/status", new { status = 3 })).Status);
        }
    }

    [Fact]
    public async Task A_developer_gets_evidence_statuses_but_never_quote_text_from_any_read_path()
    {
        await ApproveAllAsync();
        var id = IdOf("Submit leave");

        var (ls, list) = await _dev.Api.GetAsync($"/api/projects/{_projectId}/artifacts");
        Assert.Equal(200, ls);
        AssertNoQuoteText("artifact list", list!.ToJsonString());
        var listed = list.AsArray().Single(a => Id(a) == id)!;
        Assert.Equal("verified", Str(listed["data"], "evidenceStatus"));
        Assert.Equal(["verified", "verified"], listed["data"]!["evidence"]!.AsArray().Select(q => Str(q, "status")));
        Assert.Equal(["srs", "clarification"], listed["data"]!["evidence"]!.AsArray().Select(q => Str(q, "from")));
        Assert.All(listed["data"]!["evidence"]!.AsArray(), q => Assert.True(q!["quoteWithheld"]!.GetValue<bool>()));

        var (ss, single) = await _dev.Api.GetAsync($"/api/artifacts/{id}");
        Assert.Equal(200, ss);
        AssertNoQuoteText("single artifact", single!.ToJsonString());
        Assert.Equal("explicit", Str(single["data"], "requirementOrigin"));

        var nfr = (await _dev.Api.GetAsync($"/api/artifacts/{IdOf("Page load")}")).Body!;
        AssertNoQuoteText("single NFR", nfr.ToJsonString());
        Assert.Equal("quote_found", Str(nfr["data"], "targetSupport"));

        var (vs, versions) = await _dev.Api.GetAsync($"/api/artifacts/{id}/versions");
        Assert.Equal(200, vs);
        Assert.NotEmpty(versions!.AsArray());
        AssertNoQuoteText("version history", versions.ToJsonString());

        var (aus, audit) = await _dev.Api.GetAsync($"/api/projects/{_projectId}/ai-executions");
        Assert.Equal(200, aus);
        Assert.Contains(audit!.AsArray(), e => Str(e, "operationType") == "RequirementGeneration");
        AssertNoQuoteText("audit trail", audit.ToJsonString());

        var prompts = _host.Ai.UserPrompts.Count;
        var (cs, answer) = await _dev.Api.PostAsync($"/api/projects/{_projectId}/copilot/ask", new { question = "What evidence supports submitting leave?" });
        Assert.Equal(200, cs);
        AssertNoQuoteText("copilot answer", answer!.ToJsonString());
        var copilotPrompt = Assert.Single(_host.Ai.UserPrompts.Skip(prompts));
        Assert.Contains("Submit leave", copilotPrompt); // the requirement is in its context ...
        AssertNoQuoteText("copilot prompt", copilotPrompt); // ... its quotes are not
    }

    [Fact]
    public async Task Writers_and_reviewers_see_the_quotes()
    {
        var id = IdOf("Submit leave");
        Assert.Equal(SrsQuote, Str((await DataAsync(_ba, id))["evidence"]![0], "quote"));
        Assert.Equal(AnswerQuote, Str((await DataAsync(_reviewer, id))["evidence"]![1], "quote"));
        var versions = (await _reviewer.Api.GetAsync($"/api/artifacts/{id}/versions")).Body!;
        Assert.Contains(SrsQuote, versions.ToJsonString());
        Assert.Null((await DataAsync(_reviewer, id))["evidence"]![0]!["quoteWithheld"]);
    }
}
