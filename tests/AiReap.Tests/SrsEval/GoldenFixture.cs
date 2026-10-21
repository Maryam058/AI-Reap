using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiReap.Tests.SrsEval;

// Phase 0 — loads the synthetic golden fixtures (Fixtures/Srs, copied to the test output) and their
// expected results. Text is read with line endings normalized to "\n", so no result depends on how
// git checked the files out; nothing else in the text is changed.
public static partial class GoldenFixture
{
    public const string ClinicBooking = "clinic-booking";
    public const string VagueRequest = "vague-request";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Srs");

    public static string FilePath(string fileName) => Path.Combine(Directory, fileName);

    public static string SourceFileName(string name) => name == VagueRequest ? $"{name}.srs.txt" : $"{name}.srs.md";

    public static string Source(string name) => ReadNormalized(FilePath(SourceFileName(name)));

    public static FixtureExpectation Expected(string name) =>
        JsonSerializer.Deserialize<FixtureExpectation>(File.ReadAllText(FilePath($"{name}.expected.json")), Json)
        ?? throw new InvalidOperationException($"{name}.expected.json is empty.");

    public static CandidateOutput Candidate(string name) =>
        JsonSerializer.Deserialize<CandidateOutput>(File.ReadAllText(Path.Combine(Directory, "candidates", $"{name}.json")), Json)
        ?? throw new InvalidOperationException($"candidates/{name}.json is empty.");

    public static string ReadNormalized(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    // The clinic fixture with every requirement ID removed: "FR-001: Text" -> "Text", and the one line
    // that cites IDs reworded. Derived here, not stored, so it cannot drift from the numbered fixture.
    // "SHA-256" stays (a mention, never a requirement ID).
    public static string Unnumbered(string numbered) =>
        LeadingRequirementId().Replace(numbered, string.Empty)
            .Replace("Access to bookings follows the rules in FR-003 and FR-009.", "Access to bookings follows the booking and deletion rules above.");

    // The expectations of Unnumbered: the same requirements and anchors, with no source IDs to cite.
    public static FixtureExpectation UnnumberedExpected(FixtureExpectation numbered) => numbered with
    {
        Fixture = "clinic-booking (unnumbered, derived)",
        Requirements = numbered.Requirements.Select(r => r with { SourceIds = [] }).ToList(),
        Duplicates = numbered.Duplicates.Select(d => d with { SourceIds = [] }).ToList(),
        SourceIds = new SourceIdExpectation { Defined = [], MentionedOnly = ["SHA-256"] }
    };

    // The clinic fixture with `sections` filler sections inserted before "## 7. Policies summary", so a
    // small part budget puts the two halves of contradiction C1 (§4 and §7) in different parts. Filler
    // lines are unique, carry no ID, and have no trailing whitespace.
    public static string Large(string numbered, int sections = 8, int linesPerSection = 6)
    {
        var filler = new StringBuilder();
        for (var s = 1; s <= sections; s++)
        {
            filler.Append($"## 6.{s} Operations note {s}\n\n");
            for (var l = 1; l <= linesPerSection; l++)
            {
                filler.Append($"Operations note {s}.{l} describes a routine clinic activity and states no requirement.\n");
            }

            filler.Append('\n');
        }

        const string anchor = "## 7. Policies summary";
        var at = numbered.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("The clinic fixture no longer has its section 7 heading.");
        return numbered[..at] + filler + numbered[at..];
    }

    [GeneratedRegex(@"(?m)^N?FR-\d{3}: ")]
    private static partial Regex LeadingRequirementId();
}

public sealed record FixtureExpectation
{
    public string Fixture { get; init; } = string.Empty;
    public bool Synthetic { get; init; }
    public List<ExpectedRequirement> Requirements { get; init; } = [];
    public List<KeywordExpectation> AllowedDerived { get; init; } = [];
    public List<NonRequirementExpectation> NonRequirements { get; init; } = [];
    public List<PlantedExpectation> Contradictions { get; init; } = [];
    public List<PlantedExpectation> Gaps { get; init; } = [];
    public List<DuplicateExpectation> Duplicates { get; init; } = [];
    public SourceIdExpectation SourceIds { get; init; } = new();
}

// SourceIds: the IDs this requirement is written under (more than one when the source states it twice).
public sealed record ExpectedRequirement
{
    public string Key { get; init; } = string.Empty;
    public string Kind { get; init; } = "FR"; // FR | NFR
    public List<string> SourceIds { get; init; } = [];
    public List<string> AnchorQuotes { get; init; } = [];
    public List<string> Keywords { get; init; } = [];
    public string? StatedTarget { get; init; }
}

public sealed record KeywordExpectation
{
    public string Key { get; init; } = string.Empty;
    public List<string> Keywords { get; init; } = [];
}

public sealed record NonRequirementExpectation
{
    public string Key { get; init; } = string.Empty;
    public string AnchorQuote { get; init; } = string.Empty;
    public List<string> Keywords { get; init; } = [];
}

// Detected when one clarification's text contains at least one keyword of every group.
public sealed record PlantedExpectation
{
    public string Key { get; init; } = string.Empty;
    public List<string> AnchorQuotes { get; init; } = [];
    public List<List<string>> KeywordGroups { get; init; } = [];
}

public sealed record DuplicateExpectation
{
    public string Key { get; init; } = string.Empty;
    public string Requirement { get; init; } = string.Empty;
    public List<string> SourceIds { get; init; } = [];
}

public sealed record SourceIdExpectation
{
    public List<string> Defined { get; init; } = [];
    public List<string> MentionedOnly { get; init; } = [];
}

// What a run produced, in one shape whether it came from a hand-written candidate file or from the
// artifacts a service persisted (SrsQualityScorer.FromArtifacts). Quote results are never read from
// here: the scorer re-checks every quote against the source itself.
public sealed record CandidateOutput
{
    public List<CandidateRequirement> Requirements { get; init; } = [];
    public List<CandidateClarification> Clarifications { get; init; } = [];
}

public sealed record CandidateRequirement
{
    public string Kind { get; init; } = "FR"; // FR | NFR
    public string Title { get; init; } = string.Empty;
    public string Actor { get; init; } = "Receptionist";
    public string Category { get; init; } = "Performance";
    // The claim: an FR's preconditions/inputs/processing/expected result, an NFR's description.
    public string Text { get; init; } = string.Empty;
    public List<string> SourceIds { get; init; } = [];
    public string? Origin { get; init; }
    public List<CandidateQuote> Evidence { get; init; } = [];
    public string? TargetValue { get; init; }
    public string? TargetOrigin { get; init; }
}

public sealed record CandidateQuote
{
    public string? From { get; init; }
    public string Quote { get; init; } = string.Empty;
    public string? SourceId { get; init; }
    public string? Ref { get; init; }
}

public sealed record CandidateClarification
{
    public string Topic { get; init; } = string.Empty;
    public string Question { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}
