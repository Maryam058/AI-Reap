using System.Text.Json;
using System.Text.Json.Nodes;
using AiReap.Application.Ai.Pipeline;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Domain.Enums;
using Xunit;

namespace AiReap.Tests;

// P0-2 — RequirementEvidence: each quote is checked against the text it says it comes from (the full
// requirement source, a clarification answer, a document excerpt), never against another; every
// problem is a per-quote result, never an exception. Also the edit protection and S2 redaction of
// the stored fields (RequirementEvidenceFields). Pure: no database, no AI call.
public class RequirementEvidenceTests
{
    private const string Srs =
        "## 1. Leave\n\n" +
        "FR-001: Employees must be able to submit a leave request online.\n" +
        "FR-002: Managers must approve leave requests within 2 days.\n\n" +
        "## 2. Performance\n\n" +
        "NFR-001: The leave page must load within 3 seconds for 95% of requests.";

    private const string Answer = "Approval can be delegated to a deputy manager while the manager is away.";
    private const string Excerpt = "The board agreed that leave balances are recalculated every night at midnight.";

    private static RequirementEvidence Checker() => RequirementEvidence.Create(
        Srs,
        [("CQ-001", "Answered", Answer), ("CQ-002", "Open", null), ("CQ-003", "Resolved", "Only full days can be requested in the first release.")],
        [("notes.pdf#0", Excerpt), ("gone.pdf#1", null)]);

    private static EvidenceQuotePayload Q(string? from, string quote, string? sourceId = null, string? reference = null) =>
        new() { From = from, Quote = quote, SourceId = sourceId, Ref = reference };

    private static EvidenceQuotePayload One(EvidenceQuotePayload quote)
    {
        var (_, quotes) = Checker().Assess([quote]);
        return Assert.Single(quotes);
    }

    // ---- each source kind against its own text ------------------------------------------------

    [Fact]
    public void An_srs_quote_is_verified_against_the_requirement_source_at_its_cited_id()
    {
        var quote = One(Q("srs", "Employees must be able to submit a leave request online", "FR-001"));
        Assert.Equal("verified", quote.Status);
        Assert.Null(quote.Reason);
        Assert.Equal("strict", quote.MatchMode);
        Assert.Equal(1, quote.MatchCount);

        Assert.Equal("not_at_cited_id", One(Q("srs", "Employees must be able to submit a leave request online", "FR-002")).Reason);
        Assert.Equal("unknown_source_id", One(Q("srs", "Employees must be able to submit a leave request online", "FR-999")).Reason);
    }

    [Fact]
    public void A_clarification_quote_is_verified_only_against_that_clarifications_answer()
    {
        Assert.Equal("verified", One(Q("clarification", "Approval can be delegated to a deputy manager", reference: "CQ-001")).Status);
        Assert.Equal("verified", One(Q("clarification", "Only full days can be requested", reference: "cq-003")).Status);

        // The answer is not SRS text, nor is another clarification's answer.
        Assert.Equal("not_found", One(Q("srs", "Approval can be delegated to a deputy manager")).Reason);
        Assert.Equal("not_found", One(Q("clarification", "Approval can be delegated to a deputy manager", reference: "CQ-003")).Reason);
        // And SRS text is not a clarification answer.
        Assert.Equal("not_found", One(Q("clarification", "Managers must approve leave requests within 2 days", reference: "CQ-001")).Reason);
    }

    [Fact]
    public void An_excerpt_quote_is_verified_only_against_that_document_chunk()
    {
        Assert.Equal("verified", One(Q("excerpt", "leave balances are recalculated every night", reference: "notes.pdf#0")).Status);
        Assert.Equal("not_found", One(Q("srs", "leave balances are recalculated every night")).Reason);
        Assert.Equal("not_found", One(Q("excerpt", "Managers must approve leave requests within 2 days", reference: "notes.pdf#0")).Reason);
    }

    [Theory]
    [InlineData("clarification", "CQ-002", "clarification_unanswered")]
    [InlineData("clarification", "CQ-404", "unknown_reference")]
    [InlineData("clarification", null, "unknown_reference")]
    [InlineData("excerpt", "other.pdf#0", "unknown_reference")]
    [InlineData("excerpt", "notes.pdf", "unknown_reference")]
    [InlineData("excerpt", "gone.pdf#1", "source_unavailable")]
    [InlineData("email", null, "invalid_source_type")]
    [InlineData(null, null, "invalid_source_type")]
    public void Unusable_references_are_reported_on_the_quote_and_never_verified(string? from, string? reference, string reason)
    {
        var quote = One(Q(from, "Approval can be delegated to a deputy manager", reference: reference));
        Assert.Equal("unverified", quote.Status);
        Assert.Equal(reason, quote.Reason);
    }

    [Fact]
    public void An_empty_requirement_source_makes_srs_quotes_unavailable()
    {
        var (status, quotes) = RequirementEvidence.Create("").Assess([Q("srs", "Employees must be able to submit a leave request")]);
        Assert.Equal("unverified", status);
        Assert.Equal("source_unavailable", Assert.Single(quotes).Reason);
    }

    // ---- the item's status -------------------------------------------------------------------

    [Fact]
    public void One_verified_and_one_invented_quote_make_the_item_unverified_keeping_both_results()
    {
        var (status, quotes) = Checker().Assess([
            Q("srs", "Managers must approve leave requests within 2 days", "FR-002"),
            Q("srs", "Managers must approve leave requests within 3 days")]);

        Assert.Equal("unverified", status);
        Assert.Equal(["verified", "unverified"], quotes.Select(q => q.Status));
        Assert.Equal("altered", quotes[1].Reason);
    }

    [Fact]
    public void No_quote_gives_none_and_blank_quotes_are_not_kept()
    {
        Assert.Equal(("none", 0), (Checker().Assess([]).Status, Checker().Assess([]).Quotes.Count));
        Assert.Equal("none", Checker().Assess(null).Status);
        var (status, quotes) = Checker().Assess([Q("srs", "   ")]);
        Assert.Equal("none", status);
        Assert.Empty(quotes);
    }

    [Fact]
    public void At_most_two_quotes_are_kept_and_a_quote_over_forty_words_is_too_long()
    {
        var (_, kept) = Checker().Assess([
            Q("srs", "Employees must be able to submit a leave request online"),
            Q("srs", "Managers must approve leave requests within 2 days"),
            Q("srs", "The leave page must load within 3 seconds")]);
        Assert.Equal(2, kept.Count);

        var longQuote = string.Join(' ', Enumerable.Repeat("employees must be able to submit leave", 6)); // 42 words
        var quote = One(Q("srs", longQuote));
        Assert.Equal(("unverified", "too_long"), (quote.Status, quote.Reason));
    }

    [Fact]
    public void Server_owned_fields_sent_by_the_model_are_ignored()
    {
        var quote = One(new EvidenceQuotePayload
        {
            From = "srs", Quote = "a quote that is not in the source anywhere", Status = "verified", MatchCount = 9, MatchMode = "strict"
        });
        Assert.Equal("unverified", quote.Status);
        Assert.Equal(0, quote.MatchCount);
        Assert.Null(quote.MatchMode);
    }

    // ---- numbers and targets ------------------------------------------------------------------

    [Fact]
    public void Unsupported_numbers_list_claim_numbers_missing_from_verified_quotes()
    {
        var (_, quotes) = Checker().Assess([
            Q("srs", "Managers must approve leave requests within 2 days"),
            Q("srs", "Managers must approve leave requests within 30 minutes")]); // invented: does not count

        var fr = RequirementEvidence.FunctionalClaim("Request submitted", null, "Escalate after 5 days", "Approved within 2 days or 30 minutes");
        Assert.Equal(["5 days", "30 minutes"], RequirementEvidence.UnsupportedNumbers(fr, quotes));

        var (_, nfrQuotes) = Checker().Assess([Q("srs", "The leave page must load within 3 seconds for 95% of requests")]);
        Assert.Empty(RequirementEvidence.UnsupportedNumbers(RequirementEvidence.NonFunctionalClaim("Page load for 95% of requests", "3 seconds"), nfrQuotes));
        Assert.Equal(["99.9%"], RequirementEvidence.UnsupportedNumbers(RequirementEvidence.NonFunctionalClaim("Availability", "99.9%"), nfrQuotes));
    }

    [Fact]
    public void Target_support_is_quote_found_only_for_a_stated_target_in_a_verified_srs_quote()
    {
        var checker = Checker();
        var srs = checker.Assess([Q("srs", "The leave page must load within 3 seconds for 95% of requests", "NFR-001")]).Quotes;
        var invented = checker.Assess([Q("srs", "The leave page must load within 1 second for all requests")]).Quotes;
        var fromClarification = checker.Assess([Q("clarification", "Approval can be delegated to a deputy manager", reference: "CQ-001")]).Quotes;

        Assert.Equal("quote_found", RequirementEvidence.TargetSupportFor("stated", "3 seconds", srs));
        Assert.Equal("quote_found", RequirementEvidence.TargetSupportFor(" Stated ", "within 3 seconds for 95% of requests", srs));
        Assert.Equal("not_supported", RequirementEvidence.TargetSupportFor("stated", "2 seconds", srs));      // number differs
        Assert.Equal("not_supported", RequirementEvidence.TargetSupportFor("stated", "3 minutes", srs));      // unit differs
        Assert.Equal("not_supported", RequirementEvidence.TargetSupportFor("stated", "1 second", invented));  // quote not verified
        Assert.Equal("not_supported", RequirementEvidence.TargetSupportFor("stated", "deputy manager", fromClarification)); // not SRS
        Assert.Equal("not_supported", RequirementEvidence.TargetSupportFor("stated", null, srs));
        Assert.Equal("not_supported", RequirementEvidence.TargetSupportFor("stated", "3 seconds", []));
        Assert.Null(RequirementEvidence.TargetSupportFor("proposed", "3 seconds", srs));
        Assert.Null(RequirementEvidence.TargetSupportFor(null, "3 seconds", srs));
    }

    // ---- edits (RequirementEvidenceFields.SanitizeEdit) --------------------------------------

    private static string Generated(params (string Key, JsonNode? Value)[] extra)
    {
        var quotes = Checker().Assess([Q("srs", "Managers must approve leave requests within 2 days", "FR-002")]).Quotes;
        var payload = new JsonObject
        {
            ["Actor"] = "Manager",
            ["ExpectedResult"] = "Approved within 2 days",
            ["RequirementOrigin"] = "explicit",
            ["Evidence"] = JsonSerializer.SerializeToNode(quotes),
            ["EvidenceStatus"] = "verified",
            ["UnsupportedNumbers"] = new JsonArray(),
            ["EvidenceCheck"] = RequirementEvidence.CheckVersion
        };
        foreach (var (key, value) in extra) payload[key] = value;
        return payload.ToJsonString();
    }

    [Fact]
    public void An_edit_cannot_forge_server_owned_fields_in_any_key_case()
    {
        var previous = Generated();
        var edited = new JsonObject
        {
            ["actor"] = "Manager",
            ["expectedResult"] = "Approved within 2 days",
            ["requirementOrigin"] = "derived",
            ["evidence"] = new JsonArray(new JsonObject { ["from"] = "srs", ["quote"] = "Anything I like", ["status"] = "verified" }),
            ["EVIDENCESTATUS"] = "verified",
            ["evidenceStatus"] = "verified",
            ["unsupportedNumbers"] = new JsonArray("x"),
            ["evidenceCheck"] = "forged"
        };

        var result = RequirementEvidenceFields.SanitizeEdit(previous, edited, ArtifactType.FunctionalRequirement);
        var before = JsonNode.Parse(previous)!;

        Assert.Equal("derived", result["requirementOrigin"]!.GetValue<string>()); // a human may reclassify
        Assert.True(JsonNode.DeepEquals(before["Evidence"], result["Evidence"]));
        Assert.Equal("verified", result["EvidenceStatus"]!.GetValue<string>());
        Assert.Equal(RequirementEvidence.CheckVersion, result["EvidenceCheck"]!.GetValue<string>());
        Assert.Empty(result["UnsupportedNumbers"]!.AsArray());
        Assert.DoesNotContain(result, kv => kv.Key is "evidence" or "EVIDENCESTATUS" or "evidenceStatus" or "unsupportedNumbers" or "evidenceCheck");
    }

    [Fact]
    public void An_edit_of_the_claim_recalculates_unsupported_numbers_and_target_support()
    {
        var fr = RequirementEvidenceFields.SanitizeEdit(Generated(),
            new JsonObject { ["actor"] = "Manager", ["expectedResult"] = "Approved within 2 days, escalated after 4 hours" },
            ArtifactType.FunctionalRequirement);
        Assert.Equal(["4 hours"], fr["UnsupportedNumbers"]!.AsArray().Select(n => n!.GetValue<string>()));

        var nfrQuotes = Checker().Assess([Q("srs", "The leave page must load within 3 seconds for 95% of requests")]).Quotes;
        var nfr = new JsonObject
        {
            ["Category"] = "Performance", ["TargetValue"] = "3 seconds", ["TargetOrigin"] = "stated",
            ["Evidence"] = JsonSerializer.SerializeToNode(nfrQuotes), ["EvidenceStatus"] = "verified",
            ["UnsupportedNumbers"] = new JsonArray(), ["EvidenceCheck"] = RequirementEvidence.CheckVersion, ["TargetSupport"] = "quote_found"
        }.ToJsonString();

        var changed = RequirementEvidenceFields.SanitizeEdit(nfr,
            new JsonObject { ["category"] = "Performance", ["targetValue"] = "1 second", ["targetOrigin"] = "stated", ["targetSupport"] = "quote_found" },
            ArtifactType.NonFunctionalRequirement);
        Assert.Equal("not_supported", changed["TargetSupport"]!.GetValue<string>());
        Assert.Equal(["1 second"], changed["UnsupportedNumbers"]!.AsArray().Select(n => n!.GetValue<string>()));

        var proposed = RequirementEvidenceFields.SanitizeEdit(nfr,
            new JsonObject { ["category"] = "Performance", ["targetValue"] = "3 seconds", ["targetOrigin"] = "proposed" },
            ArtifactType.NonFunctionalRequirement);
        Assert.Null(proposed["TargetSupport"]);
    }

    [Fact]
    public void A_record_made_before_evidence_checks_gets_no_evidence_fields_from_an_edit()
    {
        const string legacy = """{"Actor":"Employee","ExpectedResult":"Stored within 5 seconds"}""";
        var edited = new JsonObject
        {
            ["actor"] = "Employee", ["expectedResult"] = "Stored within 5 seconds",
            ["evidenceStatus"] = "verified", ["evidence"] = new JsonArray(new JsonObject { ["quote"] = "made up" })
        };

        Assert.True(RequirementEvidenceFields.Involved(legacy, edited));
        var result = RequirementEvidenceFields.SanitizeEdit(legacy, edited, ArtifactType.FunctionalRequirement);
        Assert.Equal(["actor", "expectedResult"], result.Select(kv => kv.Key));

        Assert.False(RequirementEvidenceFields.Involved(legacy, new JsonObject { ["actor"] = "Employee" }));
    }

    // ---- S2 redaction --------------------------------------------------------------------------

    [Fact]
    public void Redaction_removes_quote_text_and_excerpt_labels_and_keeps_statuses()
    {
        var quotes = Checker().Assess([
            Q("srs", "Managers must approve leave requests within 2 days", "FR-002"),
            Q("excerpt", "leave balances are recalculated every night", reference: "notes.pdf#0")]).Quotes;
        var json = new JsonObject
        {
            ["Actor"] = "Manager", ["EvidenceStatus"] = "verified",
            ["evidence"] = JsonSerializer.SerializeToNode(quotes) // camelCase key: a human-edited record
        }.ToJsonString();

        var redacted = RequirementEvidenceFields.RedactForRestrictedCaller(json);

        Assert.DoesNotContain("Managers must approve", redacted);
        Assert.DoesNotContain("recalculated", redacted);
        Assert.DoesNotContain("notes.pdf", redacted);
        var evidence = JsonNode.Parse(redacted)!["evidence"]!.AsArray();
        Assert.Equal(["verified", "verified"], evidence.Select(q => q!["Status"]!.GetValue<string>()));
        Assert.Equal(["srs", "excerpt"], evidence.Select(q => q!["From"]!.GetValue<string>()));
        Assert.Equal("FR-002", evidence[0]!["SourceId"]!.GetValue<string>());
        Assert.All(evidence, q => Assert.True(q!["QuoteWithheld"]!.GetValue<bool>()));
        Assert.Equal("verified", JsonNode.Parse(redacted)!["EvidenceStatus"]!.GetValue<string>());

        const string legacy = """{"Actor":"Employee"}""";
        Assert.Equal(legacy, RequirementEvidenceFields.RedactForRestrictedCaller(legacy));
    }
}
