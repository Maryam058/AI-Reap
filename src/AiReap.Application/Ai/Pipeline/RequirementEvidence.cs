using System.Text.Json;
using System.Text.Json.Serialization;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

// P0-2 — checks the verbatim quotes a generated requirement cites, deterministically (no AI call),
// each against the text it says it comes from:
//   from "srs"            the requirement source's full stored RawText (never a part or a shortened
//                         prompt), at the cited sourceId's passage when one is given (SourceEvidence)
//   from "clarification"  the answer of that clarification question (Answered/Resolved) of this source
//   from "excerpt"        that project document chunk ("notes.pdf#3")
// A clarification answer or a document excerpt is never SRS evidence: only an "srs" quote counts for
// TargetSupport. Every problem is reported on the quote and never fails generation or asks for a
// repair. The model's requirementOrigin is not an input here and is never changed.
public sealed class RequirementEvidence
{
    public const string CheckVersion = "SourceEvidence-v1";

    // Decision 7: 1-2 short verbatim quotes per requirement, at most 40 words each. Quotes past the
    // second are not kept; a longer quote is reported too_long, not checked.
    public const int MaxQuotes = 2;
    public const int MaxQuoteWords = 40;

    public const string Verified = "verified", Unverified = "unverified", None = "none";
    public const string FromSrs = "srs", FromClarification = "clarification", FromExcerpt = "excerpt";
    public const string QuoteFound = "quote_found", NotSupported = "not_supported";
    public const string Stated = "stated";

    private readonly SourceEvidenceIndex _source;

    // Clarification code -> its answer's index; null when the question is not answered.
    private readonly Dictionary<string, SourceEvidenceIndex?> _clarifications;

    // Excerpt label -> the chunk's index(es); empty when the excerpt is known but its text is gone.
    private readonly Dictionary<string, List<SourceEvidenceIndex>> _excerpts;

    private RequirementEvidence(
        string? sourceText, Dictionary<string, SourceEvidenceIndex?> clarifications, Dictionary<string, List<SourceEvidenceIndex>> excerpts)
    {
        _source = SourceEvidenceIndex.Build(sourceText);
        _clarifications = clarifications;
        _excerpts = excerpts;
    }

    // clarifications: (code, ClarificationStatus, answer). excerpts: (label, chunk text; null when the
    // excerpt is known but unavailable).
    public static RequirementEvidence Create(
        string? sourceText,
        IEnumerable<(string Code, string? Status, string? Answer)>? clarifications = null,
        IEnumerable<(string Label, string? Text)>? excerpts = null)
    {
        var byCode = new Dictionary<string, SourceEvidenceIndex?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, status, answer) in clarifications ?? [])
        {
            byCode[code.Trim()] = status is "Answered" or "Resolved" && !string.IsNullOrWhiteSpace(answer)
                ? SourceEvidenceIndex.Build(answer)
                : null;
        }

        var byLabel = new Dictionary<string, List<SourceEvidenceIndex>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, text) in excerpts ?? [])
        {
            if (!byLabel.TryGetValue(label.Trim(), out var list)) byLabel[label.Trim()] = list = [];
            if (text is not null) list.Add(SourceEvidenceIndex.Build(text));
        }

        return new RequirementEvidence(sourceText, byCode, byLabel);
    }

    // Loaded once per save. shownExcerpts: the labels the model was shown; citedExcerpts: the labels
    // its quotes cite. A cited label whose chunk is gone is unavailable when it was shown or its
    // document still exists (deleted or re-indexed), and an unknown reference otherwise.
    public static async Task<RequirementEvidence> LoadAsync(
        IAiReapDbContext db, RequirementSource source, IEnumerable<string> shownExcerpts, IEnumerable<string> citedExcerpts,
        CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var questions = await db.Artifacts.AsNoTracking()
            .Where(a => a.RequirementSourceId == source.Id && a.ArtifactType == ArtifactType.ClarificationQuestion)
            .Select(a => new { a.Code, a.DataJson })
            .ToListAsync(cancellationToken);
        var clarifications = questions.Select(q =>
        {
            var payload = JsonSerializer.Deserialize<ClarificationQuestionPayload>(q.DataJson, options) ?? new ClarificationQuestionPayload();
            return (q.Code, (string?)payload.ClarificationStatus, payload.Answer);
        }).ToList();

        var shown = shownExcerpts.Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cited = citedExcerpts
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => (Label: l.Trim(), Parsed: ParseLabel(l.Trim())))
            .Where(l => l.Parsed is not null)
            .DistinctBy(l => l.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var excerpts = new List<(string Label, string? Text)>();
        if (cited.Count > 0)
        {
            var names = cited.Select(c => c.Parsed!.Value.FileName).Distinct().ToList();
            var indexes = cited.Select(c => c.Parsed!.Value.ChunkIndex).Distinct().ToList();
            var chunks = await db.DocumentChunks.AsNoTracking()
                .Where(c => c.Document!.ProjectId == source.ProjectId && names.Contains(c.Document.FileName) && indexes.Contains(c.ChunkIndex))
                .Select(c => new { c.Document!.FileName, c.ChunkIndex, c.ChunkText })
                .ToListAsync(cancellationToken);
            var documents = (await db.Documents.AsNoTracking()
                    .Where(d => d.ProjectId == source.ProjectId && names.Contains(d.FileName))
                    .Select(d => d.FileName)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (label, parsed) in cited)
            {
                var (fileName, chunkIndex) = parsed!.Value;
                var texts = chunks
                    .Where(c => string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase) && c.ChunkIndex == chunkIndex)
                    .Select(c => c.ChunkText)
                    .ToList();
                if (texts.Count > 0) excerpts.AddRange(texts.Select(t => (label, (string?)t)));
                else if (shown.Contains(label) || documents.Contains(fileName)) excerpts.Add((label, null));
            }
        }

        return Create(source.RawText, clarifications, excerpts);
    }

    // The item's quotes, each with the server's result (only the model's From/Quote/SourceId/Ref are
    // copied), and the item's status: none when no quote was given, verified only when every quote is.
    public (string Status, List<EvidenceQuotePayload> Quotes) Assess(IEnumerable<EvidenceQuotePayload>? quotes)
    {
        var checkedQuotes = (quotes ?? [])
            .Where(q => !string.IsNullOrWhiteSpace(q.Quote))
            .Take(MaxQuotes)
            .Select(CheckQuote)
            .ToList();
        var status = checkedQuotes.Count == 0 ? None
            : checkedQuotes.All(IsVerified) ? Verified
            : Unverified;
        return (status, checkedQuotes);
    }

    private EvidenceQuotePayload CheckQuote(EvidenceQuotePayload given)
    {
        var result = new EvidenceQuotePayload { From = given.From, Quote = given.Quote, SourceId = given.SourceId, Ref = given.Ref };

        EvidenceQuotePayload Fail(string reason)
        {
            result.Status = Unverified;
            result.Reason = reason;
            return result;
        }

        if (SourceEvidence.Tokenize(given.Quote).Count(t => t.IsWord) > MaxQuoteWords)
        {
            return Fail("too_long");
        }

        var key = given.Ref?.Trim() ?? string.Empty;
        switch (given.From?.Trim().ToLowerInvariant())
        {
            case FromSrs:
                return Apply(result, _source.Check(given.Quote, string.IsNullOrWhiteSpace(given.SourceId) ? null : given.SourceId));
            case FromClarification:
                if (!_clarifications.TryGetValue(key, out var answer)) return Fail("unknown_reference");
                return answer is null ? Fail("clarification_unanswered") : Apply(result, answer.Check(given.Quote));
            case FromExcerpt:
                if (!_excerpts.TryGetValue(key, out var chunks)) return Fail("unknown_reference");
                if (chunks.Count == 0) return Fail("source_unavailable");
                var checks = chunks.Select(c => c.Check(given.Quote)).ToList();
                return Apply(result, checks.FirstOrDefault(c => c.Status == EvidenceStatus.Verified) ?? checks[0]);
            default:
                return Fail("invalid_source_type");
        }
    }

    private static EvidenceQuotePayload Apply(EvidenceQuotePayload result, QuoteCheck check)
    {
        result.Status = check.Status switch
        {
            EvidenceStatus.Verified => Verified,
            EvidenceStatus.Unverified => Unverified,
            _ => None
        };
        result.Reason = check.Reason switch
        {
            EvidenceReason.NotFound => "not_found",
            EvidenceReason.Altered => "altered",
            EvidenceReason.TooShort => "too_short",
            EvidenceReason.UnknownSourceId => "unknown_source_id",
            EvidenceReason.NotAtCitedId => "not_at_cited_id",
            EvidenceReason.SourceUnavailable => "source_unavailable",
            _ => null
        };
        result.MatchMode = check.MatchMode switch
        {
            EvidenceMatchMode.Strict => "strict",
            EvidenceMatchMode.SpacingTolerant => "spacing_tolerant",
            _ => null
        };
        result.MatchCount = check.MatchCount;
        return result;
    }

    public static bool IsVerified(EvidenceQuotePayload quote) => quote.Status == Verified;

    private static bool IsFrom(EvidenceQuotePayload quote, string from) =>
        string.Equals(quote.From?.Trim(), from, StringComparison.OrdinalIgnoreCase);

    // ---- claims (also recalculated on a human edit: RequirementEvidenceFields) ---------------

    public static string FunctionalClaim(string? preconditions, string? inputs, string? processing, string? expectedResult) =>
        Claim(preconditions, inputs, processing, expectedResult);

    public static string NonFunctionalClaim(string? description, string? targetValue) => Claim(description, targetValue);

    private static string Claim(params string?[] parts) => string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    // The claim's numbers (with their units) that no verified quote contains - a warning, not a verdict.
    public static List<string> UnsupportedNumbers(string? claim, IEnumerable<EvidenceQuotePayload> quotes) =>
        SourceEvidence.NumbersMissingFromEvidence(claim, quotes.Where(IsVerified).Select(q => q.Quote)).ToList();

    // null unless the model claimed the target "stated". quote_found only when a verified "srs" quote
    // contains every number (and its unit) of the target - or, for a target without numbers, the
    // target's words; otherwise not_supported.
    public static string? TargetSupportFor(string? targetOrigin, string? targetValue, IEnumerable<EvidenceQuotePayload> quotes)
    {
        if (!string.Equals(targetOrigin?.Trim(), Stated, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var srs = quotes.Where(q => IsVerified(q) && IsFrom(q, FromSrs)).Select(q => q.Quote).ToList();
        if (string.IsNullOrWhiteSpace(targetValue) || srs.Count == 0)
        {
            return NotSupported;
        }

        if (SourceEvidence.NumbersMissingFromEvidence(targetValue, []).Count > 0)
        {
            return SourceEvidence.NumbersMissingFromEvidence(targetValue, srs).Count == 0 ? QuoteFound : NotSupported;
        }

        var target = $" {SourceEvidence.Normalize(targetValue)} ";
        return target.Trim().Length > 0 && srs.Any(q => $" {SourceEvidence.Normalize(q)} ".Contains(target, StringComparison.Ordinal))
            ? QuoteFound
            : NotSupported;
    }

    private static (string FileName, int ChunkIndex)? ParseLabel(string label)
    {
        var hash = label.LastIndexOf('#');
        return hash > 0 && int.TryParse(label[(hash + 1)..], out var index) ? (label[..hash], index) : null;
    }
}

// The P0-2 fields of an AI response are read leniently: whatever their JSON shape, they never make
// the response unparseable, so no evidence problem can cause a repair call (or fail generation).

// A string as given; another scalar or structure as its JSON text; null as null.
public sealed class LenientStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

// An array's string elements (others as their JSON text, nulls dropped); a single string as a
// one-element list; anything else as an empty list.
public sealed class LenientStringListConverter : JsonConverter<List<string>>
{
    public override bool HandleNull => true;

    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        return root.ValueKind switch
        {
            JsonValueKind.String => [root.GetString()!],
            JsonValueKind.Array => root.EnumerateArray()
                .Where(e => e.ValueKind != JsonValueKind.Null)
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText())
                .ToList(),
            _ => []
        };
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value ?? []) writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}

// Evidence quotes: only from, quote, sourceId and ref are read from the model (property names in any
// case); a bare string is a quote with no "from" (reported invalid_source_type); a single object is
// one quote; anything else is no quote. Server-owned fields the model may send are ignored.
public sealed class LenientEvidenceListConverter : JsonConverter<List<EvidenceQuotePayload>>
{
    public override bool HandleNull => true;

    public override List<EvidenceQuotePayload> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var elements = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        return elements.Select(Quote).OfType<EvidenceQuotePayload>().ToList();
    }

    private static EvidenceQuotePayload? Quote(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return new EvidenceQuotePayload { Quote = element.GetString()! };
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? Field(string name)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => property.Value.GetRawText()
                };
            }

            return null;
        }

        return new EvidenceQuotePayload { From = Field("from"), Quote = Field("quote") ?? string.Empty, SourceId = Field("sourceId"), Ref = Field("ref") };
    }

    public override void Write(Utf8JsonWriter writer, List<EvidenceQuotePayload> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var quote in value ?? [])
        {
            writer.WriteStartObject();
            writer.WriteString("from", quote.From);
            writer.WriteString("quote", quote.Quote);
            writer.WriteString("sourceId", quote.SourceId);
            writer.WriteString("ref", quote.Ref);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
