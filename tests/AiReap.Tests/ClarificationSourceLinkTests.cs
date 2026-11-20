using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AiReap.Tests;

// A clarification question's "Why it matters" links to the places in its requirement source that
// it cites. The analysis response is scripted (no live AI call); the server keeps only references that
// resolve in the source, resolves them for questions saved before they existed, withholds them from
// restricted callers (S2), and never takes them from a human edit. The source itself is opened through
// the existing GET /api/requirement-sources/{id}, which enforces project membership and S2.
public class ClarificationSourceLinkTests : IAsyncLifetime
{
    private const string AnalystMarker = "requirements analyst (§7";
    private const int Approved = 3;

    private readonly TestHost _host = new();
    private ApiUser _ba = null!, _reviewer = null!, _dev = null!, _outsider = null!;

    public async Task InitializeAsync()
    {
        _ba = await _host.RegisterAsync("BusinessAnalyst");
        _reviewer = await _host.RegisterAsync("Reviewer");
        _dev = await _host.RegisterAsync("Developer");
        _outsider = await _host.RegisterAsync("BusinessAnalyst");
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();

    private static string Analysis(params object[] missingInformation) => JsonSerializer.Serialize(new
    {
        actors = new[] { "Nurse" },
        capabilities = new[] { "Assign a triage light" },
        dataElements = Array.Empty<string>(),
        notes = Array.Empty<string>(),
        missingInformation
    });

    private async Task<(string ProjectId, string SourceId, JsonArray Questions)> AnalyzedAsync()
    {
        var (s, p) = await _ba.PostAsync("/api/projects", new { name = "Triage", description = "d" });
        Assert.Equal(201, s);
        var projectId = Id(p);
        foreach (var member in new[] { _reviewer, _dev })
        {
            Assert.InRange((await _host.AddMemberAsync(_ba, projectId, member)).Status, 200, 201);
        }

        var (ss, src) = await _ba.PostAsync($"/api/projects/{projectId}/requirement-sources", new { sourceType = 1, rawText = SourceReferenceTests.Srs });
        Assert.InRange(ss, 200, 201);
        var sourceId = Id(src);

        _host.Ai.RespondOnceWhenPromptContains(AnalystMarker, Analysis(
            new
            {
                topic = "Roles",
                question = "Does the system need to support an auditor role?",
                reason = "The SRS describes access permissions in Section 3.2 — User Roles, and Section 9.9 lists none.",
                sourceRefs = new object[]
                {
                    new { label = "Section 3.2 — User Roles", section = "3.2", heading = "User Roles", quote = "Administrators manage access permissions for every role." },
                    new { label = "Section 9.9", section = "9.9", heading = "Auditors" },
                }
            },
            new
            {
                topic = "Decline mapping",
                question = "What maps each decline category?",
                reason = "Section 12.7 is cited but gives no actual mapping."
            },
            new
            {
                topic = "Notifications",
                question = "Who is notified of a decline?",
                reason = "<script>alert('x')</script>Not stated on page 4."
            }));

        var (an, analysis) = await _ba.PostAsync($"/api/requirement-sources/{sourceId}/analyze");
        Assert.Equal(200, an);
        Assert.Equal(3, analysis!["clarificationQuestions"]!.AsArray().Count);

        var (qs, questions) = await _ba.GetAsync($"/api/requirement-sources/{sourceId}/clarification-questions");
        Assert.Equal(200, qs);
        return (projectId, sourceId, questions!.AsArray());
    }

    private static JsonNode Question(JsonArray questions, string topic) => questions.Single(q => q!["title"]!.GetValue<string>() == topic)!;

    private static JsonArray References(JsonNode question) => question["data"]!["sourceReferences"]!.AsArray();

    [Fact]
    public async Task Generated_questions_link_only_the_references_that_resolve_in_their_source()
    {
        var (_, sourceId, questions) = await AnalyzedAsync();

        var roles = References(Question(questions, "Roles"));
        var link = Assert.Single(roles)!; // Section 9.9 is not in the source: plain text
        Assert.Equal("Section 3.2 — User Roles", link["label"]!.GetValue<string>());
        Assert.Equal(sourceId, link["sourceId"]!.GetValue<string>());
        Assert.Equal("3.2 User Roles", link["heading"]!.GetValue<string>());
        Assert.True(link["excerptIsQuote"]!.GetValue<bool>());
        Assert.Equal("Administrators manage access permissions for every role.", link["excerpt"]!.GetValue<string>());

        // No sourceRefs from the model: the section number in the reason is resolved by the server.
        var decline = Assert.Single(References(Question(questions, "Decline mapping")))!;
        Assert.Equal("Section 12.7", decline["label"]!.GetValue<string>());
        Assert.Equal("12.7 Decline Categories", decline["heading"]!.GetValue<string>());

        // Markup is stored as text and a page reference is never a link.
        var notifications = Question(questions, "Notifications");
        Assert.Empty(References(notifications));
        Assert.StartsWith("<script>", notifications["data"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_question_saved_before_references_existed_gets_them_when_read_and_is_not_rewritten()
    {
        var (_, sourceId, questions) = await AnalyzedAsync();
        var id = Id(Question(questions, "Decline mapping"));
        const string legacy = """{"Question":"What maps each decline category?","Reason":"Section 12.7 is cited but gives no actual mapping.","ClarificationStatus":"Open","Answer":null}""";
        await _host.ExecuteAsync($"UPDATE Artifacts SET DataJson = N'{legacy}' WHERE Id = '{id}'");

        var read = (await _ba.GetAsync($"/api/requirement-sources/{sourceId}/clarification-questions")).Body!.AsArray();
        var link = Assert.Single(References(read.Single(q => Id(q) == id)!))!;
        Assert.Equal("Section 12.7", link["label"]!.GetValue<string>());
        Assert.Equal("12.7", link["sectionNumber"]!.GetValue<string>());

        // Resolved for the reader only: the stored question is unchanged, and answering it still works.
        Assert.Equal(1, await _host.ScalarAsync($"SELECT COUNT(*) FROM Artifacts WHERE Id = '{id}' AND DataJson NOT LIKE '%SourceReferences%'"));
        Assert.Equal(200, (await _ba.PostAsync($"/api/artifacts/{id}/clarification-answer", new { answer = "See the mapping table.", notApplicable = false })).Status);
    }

    [Fact]
    public async Task Answering_keeps_the_references_and_an_edit_cannot_forge_them()
    {
        var (_, _, questions) = await AnalyzedAsync();
        var roles = Question(questions, "Roles");
        var id = Id(roles);

        Assert.Equal(200, (await _ba.PostAsync($"/api/artifacts/{id}/clarification-answer", new { answer = "Yes.", notApplicable = false })).Status);
        var answered = (await _ba.GetAsync($"/api/artifacts/{id}")).Body!;
        Assert.Single(References(answered));

        var forged = JsonNode.Parse(answered["data"]!.ToJsonString())!.AsObject();
        forged["sourceReferences"] = new JsonArray(new JsonObject { ["label"] = "Yes", ["start"] = 0, ["line"] = 1, ["endLine"] = 2, ["heading"] = "Forged" });
        forged["question"] = "Does the system need an auditor role (edited)?";
        var (es, edited) = await _ba.SendAsync(HttpMethod.Patch, $"/api/artifacts/{id}", new { data = forged });
        Assert.Equal(200, es);
        Assert.Equal("Does the system need an auditor role (edited)?", edited!["data"]!["question"]!.GetValue<string>());
        var kept = Assert.Single(References(edited))!;
        Assert.Equal("3.2 User Roles", kept["heading"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_restricted_caller_gets_no_references_and_cannot_open_the_source()
    {
        var (_, sourceId, questions) = await AnalyzedAsync();
        var id = Id(Question(questions, "Roles"));
        Assert.Equal(200, (await _reviewer.SendAsync(HttpMethod.Patch, $"/api/artifacts/{id}/status", new { status = Approved })).Status);

        var (ds, devQuestions) = await _dev.GetAsync($"/api/requirement-sources/{sourceId}/clarification-questions");
        Assert.Equal(200, ds);
        var seen = Assert.Single(devQuestions!.AsArray())!;
        Assert.Null(seen["data"]!["sourceReferences"]);
        Assert.Null((await _dev.GetAsync($"/api/artifacts/{id}")).Body!["data"]!["sourceReferences"]);
        Assert.Equal(403, (await _dev.GetAsync($"/api/requirement-sources/{sourceId}")).Status);
    }

    [Fact]
    public async Task The_source_opens_only_for_members_of_its_project_and_an_unknown_id_is_not_found()
    {
        var (_, sourceId, _) = await AnalyzedAsync();

        var (bs, source) = await _ba.GetAsync($"/api/requirement-sources/{sourceId}");
        Assert.Equal(200, bs);
        Assert.Equal(SourceReferenceTests.Srs, source!["rawText"]!.GetValue<string>());

        Assert.Equal(403, (await _outsider.GetAsync($"/api/requirement-sources/{sourceId}")).Status);
        Assert.Equal(404, (await _ba.GetAsync($"/api/requirement-sources/{Guid.NewGuid()}")).Status);
    }
}
