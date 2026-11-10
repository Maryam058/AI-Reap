namespace AiReap.Application.Ai;

// §31 — semantic validation of structured AI output, on top of AiJsonParser's shape check. An AI
// response type opts in by implementing this; AiJsonParser runs it after deserializing and rejects
// the whole response if anything is reported (never a partial accept).
public interface IValidatableAiResponse
{
    void Validate(AiResponseValidator v);
}

public sealed class AiResponseValidator
{
    private const int MaxReportedErrors = 20;
    private readonly List<string> _errors = new();
    private readonly HashSet<string>? _knownCodes;
    private readonly HashSet<string>? _knownSourceReferences;

    private readonly SourceIdScope? _sourceIds;

    public AiResponseValidator(IEnumerable<string>? knownArtifactCodes = null, IEnumerable<string>? knownSourceReferences = null,
        SourceIdScope? sourceIds = null)
    {
        _knownCodes = knownArtifactCodes?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _knownSourceReferences = knownSourceReferences?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _sourceIds = sourceIds;
    }

    // Requirement IDs of the source (SrsIdExtractor): a requirement's own sourceIds must appear in
    // the text it was generated from; its dependsOnSourceIds anywhere in the source. Not checked
    // when the caller gives no scope.
    public void SourceIdsExist(List<string>? ids, string path, bool anywhereInSource = false)
    {
        if (_sourceIds is null || ids is null) return;
        var known = anywhereInSource ? _sourceIds.InSource : _sourceIds.InPart;
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || !known.Contains(id.Trim()))
            {
                Fail($"{path} cites '{Truncate(id)}', which does not appear in the requirement source text {(anywhereInSource ? "" : "provided ")}" +
                    "- cite only requirement IDs written in that text.");
            }
        }
    }

    // §26 citations ("notes.pdf#3") must point at an excerpt the model was actually given.
    public void SourceReferencesExist(List<string>? references, string path)
    {
        if (references is null) return;
        foreach (var reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference) || _knownSourceReferences is null || !_knownSourceReferences.Contains(reference.Trim()))
            {
                Fail($"{path} cites '{Truncate(reference)}', which is not one of the document excerpts provided.");
            }
        }
    }

    public IReadOnlyList<string> Errors => _errors;
    public bool IsValid => _errors.Count == 0;

    public void Fail(string error)
    {
        if (_errors.Count < MaxReportedErrors) _errors.Add(error);
    }

    public void Required(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value)) Fail($"{path} is required and must not be empty.");
    }

    public void MaxLength(string? value, int max, string path)
    {
        if (value is not null && value.Length > max) Fail($"{path} is longer than {max} characters.");
    }

    // Case-insensitive; a null/blank value is allowed only when optional.
    public void OneOf(string? value, string path, IReadOnlyCollection<string> allowed, bool optional = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!optional) Fail($"{path} is required; expected one of: {string.Join(", ", allowed)}.");
            return;
        }

        if (!allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            Fail($"{path} has value '{Truncate(value)}'; expected one of: {string.Join(", ", allowed)}.");
        }
    }

    // A JSON array present but containing nulls deserializes to null elements - an incomplete
    // response, not something to skip over silently.
    public void Items<T>(List<T>? items, string path, Action<T, string> validateItem, int minItems = 0) where T : class
    {
        if (items is null)
        {
            Fail($"{path} is missing.");
            return;
        }

        if (items.Count < minItems) Fail($"{path} must contain at least {minItems} item(s).");

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is null)
            {
                Fail($"{path}[{i}] is null.");
                continue;
            }

            validateItem(items[i], $"{path}[{i}]");
        }
    }

    public void NoBlankEntries(List<string>? values, string path)
    {
        if (values is null) return;
        for (var i = 0; i < values.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(values[i])) Fail($"{path}[{i}] is empty.");
        }
    }

    // Only checked when the caller supplied the set of codes that actually exist (the artifacts
    // that were given to the model as context) - otherwise the model could "link" to invented ids.
    public void CodesExist(List<string>? codes, string path)
    {
        if (_knownCodes is null || codes is null) return;
        foreach (var code in codes)
        {
            if (string.IsNullOrWhiteSpace(code) || !_knownCodes.Contains(code.Trim()))
            {
                Fail($"{path} references '{Truncate(code)}', which is not one of the artifacts provided.");
            }
        }
    }

    // A4b — an artifact that must trace to the requirements it was given cites at least one of them.
    // Only when some were given: with none known (a source without FRs) an empty list stays valid.
    // Which codes are cited is CodesExist's check; this only rejects citing nothing.
    public void CodesRequired(List<string>? codes, string path)
    {
        if (_knownCodes is not { Count: > 0 }) return;
        if (codes is null || codes.All(string.IsNullOrWhiteSpace))
        {
            Fail($"{path} must cite at least one of the artifacts provided.");
        }
    }

    public void CodeExists(string? code, string path)
    {
        if (_knownCodes is null) return;
        if (string.IsNullOrWhiteSpace(code) || !_knownCodes.Contains(code.Trim()))
        {
            Fail($"{path} references '{Truncate(code)}', which is not one of the artifacts provided.");
        }
    }

    private static string Truncate(string? value) =>
        value is null ? "" : value.Length <= 60 ? value : value[..60] + "...";
}

public static class AiVocabulary
{
    public static readonly string[] Priorities = ["Low", "Medium", "High", "Critical"];

    // §10 — the eight NFR categories.
    public static readonly string[] NfrCategories =
        ["Performance", "Security", "Availability", "Scalability", "Usability", "Maintainability", "Compliance", "Observability"];

    public static readonly string[] AcceptanceCriterionKinds = ["positive", "negative", "boundary"];
    public static readonly string[] TestKinds = ["positive", "negative", "boundary", "permission", "validation"];

    // Test cases for a non-functional requirement: the quality being verified, or one of TestKinds.
    public static readonly string[] NfrTestKinds =
        ["performance", "load", "security", "availability", "reliability", "scalability", "usability", "accessibility",
         "maintainability", "compliance", "observability"];
    public static readonly string[] TaskTypes = ["Backend", "Frontend", "Database", "Infrastructure", "Testing", "Documentation"];
    public static readonly string[] HttpMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];
    public static readonly string[] ConflictTypes = ["ConflictsWith", "DuplicateOf"];
}
