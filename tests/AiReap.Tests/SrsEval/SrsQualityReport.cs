using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiReap.Tests.SrsEval;

// Phase 0 — the scorer's result. Rendered deterministically (fixed property order, no timestamps,
// invariant culture), so the same input always gives byte-identical JSON and Markdown. Written to
// xUnit test output only; nothing is written to disk.
public sealed record SrsQualityReport
{
    public string Fixture { get; init; } = string.Empty;
    public RequirementScores Requirements { get; init; } = new();
    public CitationScores Citations { get; init; } = new();
    public NumberScores Numbers { get; init; } = new();
    public List<DuplicateResult> Duplicates { get; init; } = [];
    public AnalysisScores Analysis { get; init; } = new();
    public List<ItemResult> Items { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions).Replace("\r\n", "\n");

    public string ToMarkdown()
    {
        var r = Requirements;
        var sb = new StringBuilder();
        sb.Append($"## SRS quality report: {Fixture}\n\n");
        sb.Append("| Metric | Value |\n|---|---|\n");
        void Row(string name, object value) => sb.Append($"| {name} | {value} |\n");
        Row("Recall (all matches)", r.Recall);
        Row("Grounded recall (source ID or verified quote)", r.GroundedRecall);
        Row("Keyword-only matches (recall only, never evidence)", r.KeywordOnlyMatches);
        Row("FR recall", r.FrRecall);
        Row("NFR recall", r.NfrRecall);
        Row("Precision", r.Precision);
        Row("Unmerged duplicates", r.UnmergedDuplicates);
        Row("Background text promoted to a requirement", r.BackgroundPromoted);
        Row("Unmatched items", r.Unmatched);
        Row("Quotes verified", Citations.QuotesVerified);
        Row("Items with a verified SRS quote", Citations.ItemsWithVerifiedSrsQuote);
        Row("Cited source IDs that are defined", Citations.CitedSourceIdsDefined);
        Row("'explicit' without a verified SRS quote", Citations.ExplicitWithoutVerifiedSrsQuote);
        Row("Source-ID coverage", Citations.CoverageStatus);
        Row("Items with unsupported numbers", Numbers.ItemsWithUnsupportedNumbers);
        Row("Items with numbers not in the source", Numbers.ItemsWithNumbersNotInSource);
        Row("Targets claimed stated but not supported", Numbers.StatedTargetsNotSupported);
        Row("Stated-target accuracy", Numbers.StatedTargetAccuracy);
        Row("Gap recall", Analysis.GapRecall);
        Row("Contradiction recall (keyword heuristic)", Analysis.ContradictionRecallHeuristic);
        Row("Redundant clarification pairs", Analysis.RedundantClarificationPairs.Count);
        if (r.Missing.Count > 0) sb.Append($"\nMissing: {string.Join(", ", r.Missing)}\n");
        if (Citations.UncoveredSourceIds.Count > 0) sb.Append($"Uncovered source IDs: {string.Join(", ", Citations.UncoveredSourceIds)}\n");
        if (Analysis.GapsMissed.Count > 0) sb.Append($"Gaps missed: {string.Join(", ", Analysis.GapsMissed)}\n");
        if (Analysis.ContradictionsMissed.Count > 0) sb.Append($"Contradictions missed: {string.Join(", ", Analysis.ContradictionsMissed)}\n");
        return sb.ToString();
    }
}

public sealed record RequirementScores
{
    public int Expected { get; init; }
    public int Generated { get; init; }
    public Ratio Recall { get; init; } = new(0, 0);
    public Ratio GroundedRecall { get; init; } = new(0, 0);
    public int KeywordOnlyMatches { get; init; }
    public Ratio FrRecall { get; init; } = new(0, 0);
    public Ratio NfrRecall { get; init; } = new(0, 0);
    public Ratio Precision { get; init; } = new(0, 0);
    public int UnmergedDuplicates { get; init; }
    public int BackgroundPromoted { get; init; }
    public int Unmatched { get; init; }
    public int AllowedDerived { get; init; }
    public List<string> Missing { get; init; } = [];
}

public sealed record CitationScores
{
    public Ratio QuotesVerified { get; init; } = new(0, 0);
    public Ratio ItemsWithVerifiedSrsQuote { get; init; } = new(0, 0);
    public Ratio CitedSourceIdsDefined { get; init; } = new(0, 0);
    public int ExplicitWithoutVerifiedSrsQuote { get; init; }
    public string CoverageStatus { get; init; } = string.Empty;
    public List<string> UncoveredSourceIds { get; init; } = [];
}

public sealed record NumberScores
{
    public int ItemsWithUnsupportedNumbers { get; init; }
    public int ItemsWithNumbersNotInSource { get; init; }
    public int StatedTargetsNotSupported { get; init; }
    public Ratio StatedTargetAccuracy { get; init; } = new(0, 0);
}

public sealed record AnalysisScores
{
    public int Clarifications { get; init; }
    public Ratio GapRecall { get; init; } = new(0, 0);
    public List<string> GapsDetected { get; init; } = [];
    public List<string> GapsMissed { get; init; } = [];
    public Ratio ContradictionRecallHeuristic { get; init; } = new(0, 0);
    public List<string> ContradictionsDetected { get; init; } = [];
    public List<string> ContradictionsMissed { get; init; } = [];
    public List<string> RedundantClarificationPairs { get; init; } = [];
}

public sealed record DuplicateResult(string Key, string Requirement, bool Handled, int ExtraItems, bool CitesAllSourceIds);

public sealed record ItemResult(
    int Index, string Kind, string Title, string Outcome, string? MatchedKey, string EvidenceStatus,
    List<string> UnsupportedNumbers, List<string> NumbersNotInSource, string? TargetSupport);

// A count out of a total; "n/a" when there is nothing to count.
[JsonConverter(typeof(RatioJsonConverter))]
public sealed record Ratio(int Numerator, int Denominator)
{
    public override string ToString() => Denominator == 0
        ? $"n/a ({Numerator}/0)"
        : $"{(Numerator / (double)Denominator).ToString("0.000", CultureInfo.InvariantCulture)} ({Numerator}/{Denominator})";
}

public sealed class RatioJsonConverter : JsonConverter<Ratio>
{
    public override Ratio Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Reports are written, not read.");

    public override void Write(Utf8JsonWriter writer, Ratio value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
