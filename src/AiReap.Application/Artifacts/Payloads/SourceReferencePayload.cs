namespace AiReap.Application.Artifacts.Payloads;

// One place in a clarification question's Reason that points at its requirement source ("Section 3.2",
// "§4.1", a quoted heading title), resolved by the server against the source's own text
// (SourceReferences.Resolve). Only references that resolve are stored, so every one of these is a
// location that exists in the retained source text. The source text is retained verbatim and never
// changes (§6), so Line/EndLine stay valid for the life of the source.
public class SourceReferencePayload
{
    // The text of the link, exactly as written in Reason at index Start.
    public string Label { get; set; } = string.Empty;
    public int Start { get; set; }

    // The RequirementSource the reference resolves into (the question's own source).
    public Guid SourceId { get; set; }

    // The heading the reference resolves to, as written in the source ("3.2 User Roles"), and its
    // number when it has one ("3.2"). Null for a quote outside any recognised section.
    public string? SectionNumber { get; set; }
    public string? Heading { get; set; }

    // 0-based line range in the source text (lines split on \r\n, \r or \n), EndLine exclusive:
    // the whole section for a section reference, the quoted passage for a quote reference.
    public int Line { get; set; }
    public int EndLine { get; set; }
    public string Anchor { get; set; } = string.Empty; // "L{Line}" - stable while the source text is

    // A short passage copied from the source text: the model's quote when it was found there
    // (ExcerptIsQuote), otherwise the start of the section.
    public string? Excerpt { get; set; }
    public bool ExcerptIsQuote { get; set; }
}
