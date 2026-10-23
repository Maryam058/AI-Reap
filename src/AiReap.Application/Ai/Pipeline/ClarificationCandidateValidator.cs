using System.Text.RegularExpressions;
using AiReap.Application.Artifacts.Payloads;

namespace AiReap.Application.Ai.Pipeline;

// One clarification question the analysis model proposed (RequirementAnalysis-v3 missingInformation item).
// SourceRefs: the places the model claimed for its reason - only used to tell apart the places of a
// quote written more than once (ClarificationEvidenceLocator), never as evidence themselves.
public sealed record ClarificationCandidate(
    string Topic, string Question, string? Reason, string? Kind, string? Impact, IReadOnlyList<string> EvidenceQuotes,
    IReadOnlyList<ClaimedSourceReference>? SourceRefs = null);

// A clarification question that already exists for the source (any status), for duplicate and
// already-answered checks. EvidenceLines: the source lines its stored evidence covers (empty if none).
// Questions saved before RequirementAnalysis-v3 have no Kind and no evidence: for those, Reason and
// SourceReferences (its resolved links, null when never resolved) say where in the source it points,
// and Answer is what a person answered.
public sealed record ExistingClarification(
    string Code, string Question, string? Topic, string Status, IReadOnlySet<int> EvidenceLines, string? Kind = null,
    string? Reason = null, string? Answer = null, IReadOnlyList<SourceReferencePayload>? SourceReferences = null);

public sealed record CandidateRejection(string Code, string Message, string? RelatedCode = null, string? Location = null);

// Outcome for one candidate: Accepted with its located evidence, or the reasons it was rejected.
public sealed record CandidateVerdict(
    ClarificationCandidate Candidate,
    string? Kind,
    IReadOnlyList<SourceReferencePayload> Evidence,
    IReadOnlyList<string> RequirementRefs,
    IReadOnlyList<CandidateRejection> Rejections)
{
    public bool Accepted => Rejections.Count == 0;
}

// Deterministic acceptance of AI-proposed clarification questions (no AI call, no I/O). The model that
// proposed a question never certifies it: a question is saved only when every check below passes, and
// every rejection is reported with its reason. Checks, in order (all are run, so a rejected candidate
// lists every reason that applies):
//   missing-fields       question, reason (what is missing or conflicting) and impact are all required;
//   not-a-question       the question does not end with "?";
//   multiple-questions   more than one "?" - one question per clarification;
//   invalid-kind         kind is not missing | ambiguous | contradiction | undefined-rule (synonyms map);
//   generic              a best-practice / open-ended prompt ("any other requirements?") rather than an
//                        evidenced uncertainty in this source;
//   no-evidence          no evidence quote was found verbatim in the source (SourceEvidenceIndex). A quote
//                        of the whole source counts, so a one-line source can still be questioned;
//   contradiction-needs-both-statements  a contradiction needs two verified quotes at different places;
//   unrelated            the question shares no content term with its own evidence or the headings of
//                        the sections that evidence sits in;
//   answered-in-source   another passage of the source answers the question. A passage is a sentence,
//                        or a table row read with its column headers ("Approver: Line manager"). It
//                        answers when it (not the evidence itself, nor a restatement of it, nor a heading
//                        or a question) contains at least AnsweredCoverage of the question's terms, does
//                        not leave the point open ("TBD", "as required") or defer it elsewhere ("see the
//                        HR policy", "as defined in Appendix C"), and gives the kind of answer the
//                        question asks for (QuestionFocus: a role for "who", a time for "when", a number
//                        for "how many", something beyond the question's own words otherwise). A question
//                        term it lacks may only be one the question took from its evidence (not a
//                        qualifier of its own: "for contractors"). A table row must also hold every
//                        question term that tells its table's rows apart ("annual" for annual leave, not
//                        the sick leave row), and its open cells ("TBD") give no answer. Sections the evidence or
//                        reason refers to ("See Appendix B", "section 4.2") are searched first and the
//                        rejection names the reference followed; a reference that does not resolve adds
//                        nothing. A section heading is never counted as part of the answer. Skipped for
//                        contradictions;
//   duplicate / already-answered  the same question - exact after normalization, or term overlap
//                        (Jaccard) >= DuplicateSimilarity, or the same topic at >= TopicDuplicateSimilarity,
//                        or the same kind on the same evidence lines at >= SameEvidenceSimilarity - as an existing question
//                        for the source (already-answered when that one is Answered/Resolved/NotApplicable)
//                        or as one accepted earlier in the same batch. A question saved before v3 (no kind,
//                        no evidence) also matches when its question and topic hold at least
//                        AnsweredCoverage of the candidate's terms, unless both are located in the source
//                        and in different sections; a missing location is not taken as a difference. A
//                        closed question with an answer matches when its question, topic and answer hold
//                        AnsweredCoverage of the candidate's terms. Never a match when the two ask for
//                        different things (QuestionFocus: "who" against "when").
// Limits: all similarity is lexical (light stemming and a short list of synonyms - approve/authorise,
// remove/delete, ...), so a paraphrase with other vocabulary can still pass the duplicate and
// answered-in-source checks, and a passage that matches the words and the kind of answer without really
// answering can reject a question - the rejection quotes the passage so a reviewer can see. Existing
// questions are only compared against, never changed.
public static partial class ClarificationCandidateValidator
{
    public const string Version = "CqValidation-v1";

    public const double DuplicateSimilarity = 0.6;
    public const double TopicDuplicateSimilarity = 0.4;
    public const double SameEvidenceSimilarity = 0.25;
    public const double AnsweredCoverage = 0.75;
    public const double RestatementSimilarity = 0.8;
    public const int MaxEvidenceQuotes = 4;

    public static readonly IReadOnlyList<string> Kinds = ["missing", "ambiguous", "contradiction", "undefined-rule"];

    private static readonly HashSet<string> ClosedStatuses = new(StringComparer.OrdinalIgnoreCase) { "Answered", "Resolved", "NotApplicable" };

    public sealed class Context
    {
        private readonly List<Known> _known = [];

        public Context(Guid sourceId, string? sourceText, IEnumerable<ExistingClarification> existing)
        {
            SourceText = sourceText ?? string.Empty;
            References = SourceReferenceIndex.Build(sourceId, SourceText);
            Evidence = SourceEvidenceIndex.Build(SourceText);
            Locator = new ClarificationEvidenceLocator(References, Evidence, SourceText);
            Sentences = SplitSentences(SourceText);
            NormalizedSource = SourceEvidence.Normalize(SourceText);
            foreach (var e in existing)
            {
                var kind = NormalizeKind(e.Kind);
                var legacy = kind is null && e.EvidenceLines.Count == 0;
                var terms = Terms(e.Question);
                var context = Terms(e.Question);
                context.UnionWith(Terms(e.Topic));
                HashSet<string>? answered = null;
                if (ClosedStatuses.Contains(e.Status) && !string.IsNullOrWhiteSpace(e.Answer))
                {
                    answered = new HashSet<string>(context, StringComparer.Ordinal);
                    answered.UnionWith(Terms(e.Answer));
                }

                // Where the question points: its evidence, else (a pre-v3 question) its resolved links, or
                // the links its reason resolves to now. None found means "not known", not "elsewhere".
                var places = SectionsOfLines(e.EvidenceLines);
                if (legacy)
                {
                    var links = e.SourceReferences ?? References.Resolve(e.Reason);
                    places = links.Where(r => r.Line >= 0).SelectMany(r => r.ExcerptIsQuote
                            ? SectionsOfLines(Enumerable.Range(r.Line, Math.Max(r.EndLine - r.Line, 1)))
                            : References.Sections.Where(s => s.Line == r.Line && s.EndLine == r.EndLine))
                        .Distinct().ToList();
                }

                _known.Add(new Known(AiResultMerge.Key(e.Question), terms, TopicKey(e.Topic), e.EvidenceLines.ToHashSet(),
                    kind, e.Code, e.Status, context, answered, places, QuestionFocus(e.Question), legacy));
            }
        }

        public string SourceText { get; }
        public SourceReferenceIndex References { get; }
        public SourceEvidenceIndex Evidence { get; }
        public ClarificationEvidenceLocator Locator { get; }
        internal IReadOnlyList<Sentence> Sentences { get; }
        internal string NormalizedSource { get; }

        internal IEnumerable<Known> KnownQuestions => _known;

        internal void Remember(ClarificationCandidate candidate, string? kind, IEnumerable<SourceReferencePayload> evidence)
        {
            var lines = LinesOf(evidence);
            var context = Terms(candidate.Question);
            context.UnionWith(Terms(candidate.Topic));
            _known.Add(new Known(AiResultMerge.Key(candidate.Question), Terms(candidate.Question), TopicKey(candidate.Topic),
                lines, kind, null, "Accepted in this run", context, null, SectionsOfLines(lines), QuestionFocus(candidate.Question), false));
        }

        // The innermost sections (table of contents left out) the lines sit in.
        internal List<SourceSection> SectionsOfLines(IEnumerable<int> lines) => lines
            .Select(l => References.Sections.Where(s => !s.TableOfContents && s.Line <= l && l < s.EndLine).MaxBy(s => s.Line))
            .OfType<SourceSection>()
            .Distinct()
            .ToList();
    }

    // A question already saved for the source, or accepted earlier in this run (Code null). ContextTerms:
    // its question and topic; AnsweredTerms: those and its answer, when it is closed with one; Places: the
    // sections it is located in (empty when not known); Legacy: saved before v3, without kind or evidence.
    internal sealed record Known(
        string Key, HashSet<string> Terms, string? TopicKey, HashSet<int> Lines, string? Kind, string? Code, string Status,
        HashSet<string> ContextTerms, HashSet<string>? AnsweredTerms, IReadOnlyList<SourceSection> Places,
        IReadOnlySet<string> Focus, bool Legacy);

    // Validates candidates in order; an accepted one counts for the duplicate check of later ones.
    public static List<CandidateVerdict> Validate(Context context, IEnumerable<ClarificationCandidate> candidates)
    {
        var verdicts = new List<CandidateVerdict>();
        foreach (var candidate in candidates)
        {
            var verdict = ValidateOne(context, candidate);
            if (verdict.Accepted)
            {
                context.Remember(candidate, verdict.Kind, verdict.Evidence);
            }

            verdicts.Add(verdict);
        }

        return verdicts;
    }

    public static CandidateVerdict ValidateOne(Context context, ClarificationCandidate candidate)
    {
        var rejections = new List<CandidateRejection>();
        var question = candidate.Question?.Trim() ?? string.Empty;

        var missing = new List<string>();
        if (question.Length == 0) missing.Add("question");
        if (string.IsNullOrWhiteSpace(candidate.Reason)) missing.Add("reason (what is missing or conflicting)");
        if (string.IsNullOrWhiteSpace(candidate.Impact)) missing.Add("impact (why the answer matters)");
        if (missing.Count > 0)
        {
            rejections.Add(new("missing-fields", $"Missing {string.Join(", ", missing)}."));
        }

        if (question.Length > 0 && !question.EndsWith('?'))
        {
            rejections.Add(new("not-a-question", "The question does not end with a question mark."));
        }

        if (question.Count(c => c == '?') > 1)
        {
            rejections.Add(new("multiple-questions", "More than one question was asked; a clarification asks exactly one."));
        }

        var kind = NormalizeKind(candidate.Kind);
        if (kind is null)
        {
            rejections.Add(new("invalid-kind", string.IsNullOrWhiteSpace(candidate.Kind)
                ? $"No kind was given (one of {string.Join(", ", Kinds)})."
                : $"Kind '{candidate.Kind}' is not one of {string.Join(", ", Kinds)}."));
        }

        if (GenericQuestion().IsMatch(question))
        {
            rejections.Add(new("generic", "A generic or open-ended question, not an evidenced uncertainty in this source."));
        }

        var evidence = LocateEvidence(context, candidate);
        if (evidence.Count == 0)
        {
            rejections.Add(new("no-evidence", candidate.EvidenceQuotes.Count == 0
                ? "No evidence quote from the source was given."
                : "None of the evidence quotes was found verbatim in the source."));
        }
        else if (kind == "contradiction" && DistinctPlaces(evidence) < 2)
        {
            rejections.Add(new("contradiction-needs-both-statements",
                "A contradiction must quote both conflicting statements, found at different places in the source."));
        }

        var questionTerms = Terms(question);
        if (evidence.Count > 0 && !Relevant(questionTerms, evidence))
        {
            rejections.Add(new("unrelated", "The question shares no content with the evidence it cites."));
        }

        if (evidence.Count > 0 && kind is not "contradiction" && AnsweredElsewhere(context, candidate, questionTerms, evidence) is { } answer)
        {
            rejections.Add(new("answered-in-source",
                $"The source already states this at {answer.Location}: \"{Shorten(answer.Text)}\"", Location: answer.Location));
        }

        if (question.Length > 0 && DuplicateOf(context, candidate, kind, questionTerms, evidence) is { } duplicate)
        {
            var closed = ClosedStatuses.Contains(duplicate.Status);
            var name = duplicate.Code ?? "a question accepted earlier in this run";
            rejections.Add(closed
                ? new("already-answered", $"Already asked and {duplicate.Status.ToLowerInvariant()} as {name}.", duplicate.Code)
                : new("duplicate", $"Duplicate of {name}.", duplicate.Code));
        }

        return new CandidateVerdict(candidate, kind, evidence, RequirementRefsOf(evidence), rejections);
    }

    public static string? NormalizeKind(string? kind)
    {
        var key = Regex.Replace((kind ?? string.Empty).Trim().ToLowerInvariant(), @"[\s_]+", "-");
        return key switch
        {
            "missing" or "missing-information" or "gap" or "incomplete" => "missing",
            "ambiguous" or "ambiguity" or "vague" or "unclear" => "ambiguous",
            "contradiction" or "contradictory" or "conflict" or "conflicting" or "inconsistency" => "contradiction",
            "undefined-rule" or "undefined-business-rule" or "undefined-acceptance" or "undefined-acceptance-condition"
                or "business-rule" or "acceptance" => "undefined-rule",
            _ => null
        };
    }

    // ---- evidence ------------------------------------------------------------------------------

    // The quotes verified in the source, each at its place (ClarificationEvidenceLocator); a quote written
    // in several places is placed by the sections the reason and the model's claims cite.
    private static List<SourceReferencePayload> LocateEvidence(Context context, ClarificationCandidate candidate) =>
        context.Locator.Locate(
            candidate.EvidenceQuotes.Take(MaxEvidenceQuotes),
            context.References.Resolve(candidate.Reason, candidate.SourceRefs));

    private static int DistinctPlaces(IReadOnlyList<SourceReferencePayload> evidence)
    {
        var places = new List<(int Line, int EndLine)>();
        foreach (var e in evidence)
        {
            if (e.Line < 0)
            {
                places.Add((-1 - places.Count, -1 - places.Count)); // unlocated: count it as its own place
                continue;
            }

            if (!places.Any(p => p.Line >= 0 && e.Line < p.EndLine && p.Line < e.EndLine))
            {
                places.Add((e.Line, e.EndLine));
            }
        }

        return places.Count;
    }

    private static HashSet<int> LinesOf(IEnumerable<SourceReferencePayload> evidence)
    {
        var lines = new HashSet<int>();
        foreach (var e in evidence.Where(e => e.Line >= 0))
        {
            for (var l = e.Line; l < Math.Max(e.EndLine, e.Line + 1); l++) lines.Add(l);
        }

        return lines;
    }

    // What evidence concerns: the requirement IDs in each passage, else its section heading, else its line.
    public static List<string> RequirementRefsOf(IReadOnlyList<SourceReferencePayload> evidence)
    {
        var refs = new List<string>();
        foreach (var e in evidence)
        {
            var ids = SrsIdExtractor.Extract(e.Excerpt ?? string.Empty);
            var reference = ids.Count > 0
                ? string.Join(", ", ids)
                : e.Heading is { Length: > 0 } heading
                    ? heading.TrimStart('#', ' ')
                    : e.Line >= 0 ? $"line {e.Line + 1}" : null;
            if (reference is not null && !refs.Contains(reference, StringComparer.OrdinalIgnoreCase))
            {
                refs.Add(reference);
            }
        }

        return refs;
    }

    private static bool Relevant(HashSet<string> questionTerms, IReadOnlyList<SourceReferencePayload> evidence)
    {
        if (questionTerms.Count == 0)
        {
            return false;
        }

        var context = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in evidence)
        {
            context.UnionWith(Terms(e.Excerpt));
            context.UnionWith(Terms(e.Heading));
        }

        return questionTerms.Overlaps(context);
    }

    // ---- answered elsewhere ----------------------------------------------------------------------

    // A passage that may answer a question: a sentence, or a table row (Table >= 0: the index of its
    // table) whose Text reads each cell with its column header ("Approver: Line manager; ...") and whose
    // Terms include the headers'. Cells: a table row's cells, for the person check.
    internal sealed record Sentence(
        int Line, string Text, HashSet<string> Terms, bool Heading, int Table = -1, HashSet<string>? CellTerms = null,
        IReadOnlyList<(string Header, string Cell)>? Cells = null);

    private static (string Location, string Text)? AnsweredElsewhere(
        Context context, ClarificationCandidate candidate, HashSet<string> questionTerms, IReadOnlyList<SourceReferencePayload> evidence)
    {
        if (questionTerms.Count < 2)
        {
            return null;
        }

        var question = candidate.Question.Trim();
        var focus = QuestionFocus(question);
        var evidenceLines = LinesOf(evidence);
        var evidenceTerms = evidence.Select(e => Terms(e.Excerpt)).Where(t => t.Count > 0).ToList();
        var evidenceWords = evidenceTerms.SelectMany(t => t).ToHashSet(StringComparer.Ordinal);

        string Locate(Sentence sentence)
        {
            var section = context.SectionsOfLines([sentence.Line]).FirstOrDefault();
            return section is not null ? $"{section.HeadingLine.TrimStart('#', ' ')} (line {sentence.Line + 1})" : $"line {sentence.Line + 1}";
        }

        // The sections the evidence and the reason refer to, followed first so the rejection can say so.
        foreach (var (reference, from) in FollowedReferences(context, candidate, evidence))
        {
            foreach (var sentence in context.Sentences.Where(s => s.Line >= reference.Line && s.Line < reference.EndLine))
            {
                if (!evidenceLines.Contains(sentence.Line) && Answers(context, sentence, question, questionTerms, focus, evidenceTerms, evidenceWords))
                {
                    return ($"{Locate(sentence)}, reached from \"{Shorten(reference.Label)}\" ({from})", sentence.Text);
                }
            }
        }

        foreach (var sentence in context.Sentences)
        {
            if (!evidenceLines.Contains(sentence.Line) && Answers(context, sentence, question, questionTerms, focus, evidenceTerms, evidenceWords))
            {
                return (Locate(sentence), sentence.Text);
            }
        }

        return null;
    }

    // Section references in the evidence lines and in the candidate's reason that resolve to a section of
    // the source (SourceReferenceIndex.Resolve), with where each was written.
    private static List<(SourceReferencePayload Reference, string From)> FollowedReferences(
        Context context, ClarificationCandidate candidate, IReadOnlyList<SourceReferencePayload> evidence)
    {
        var lines = SourceReferenceIndex.Lines(context.SourceText);
        var followed = new List<(SourceReferencePayload, string)>();
        void Add(string? text, string from)
        {
            foreach (var reference in context.References.Resolve(text).Where(r => !r.ExcerptIsQuote && r.Line >= 0))
            {
                if (!followed.Any(f => f.Item1.Line == reference.Line && f.Item1.EndLine == reference.EndLine))
                {
                    followed.Add((reference, from));
                }
            }
        }

        foreach (var line in LinesOf(evidence).Order())
        {
            Add(line < lines.Length ? lines[line] : null, $"line {line + 1}");
        }

        Add(candidate.Reason, "the question's reason");
        return followed;
    }

    // Whether one passage answers the question (see the class comment, answered-in-source). A table
    // row's open cells ("TBD") are left out of its answer rather than voiding the row.
    private static bool Answers(
        Context context, Sentence sentence, string question, HashSet<string> questionTerms, IReadOnlySet<string> focus,
        List<HashSet<string>> evidenceTerms, HashSet<string> evidenceWords)
    {
        if (sentence.Heading || sentence.Text.EndsWith('?')
            || (sentence.Table < 0 && (Vague().IsMatch(sentence.Text) || Deferral().IsMatch(sentence.Text))))
        {
            return false;
        }

        var shared = questionTerms.Count(sentence.Terms.Contains);
        if (shared < 2 || shared < AnsweredCoverage * questionTerms.Count)
        {
            return false;
        }

        // A question term the passage lacks may only be one the question took from its own evidence
        // ("request" in "Each leave request is routed..."): a qualifier of the question's own ("for
        // contractors", "parental") that the passage does not mention means it answers something else.
        if (questionTerms.Any(t => !sentence.Terms.Contains(t) && !evidenceWords.Contains(t)))
        {
            return false;
        }

        // A row of a table must hold the question's terms that tell the table's rows apart.
        if (sentence.Table >= 0)
        {
            var siblings = context.Sentences.Where(s => s.Table == sentence.Table && !s.Heading && s != sentence).ToList();
            if (questionTerms.Any(t => !sentence.CellTerms!.Contains(t) && siblings.Any(s => s.CellTerms!.Contains(t))))
            {
                return false;
            }
        }

        // A restatement of the evidence ("Summary of requirements") adds no answer.
        if (evidenceTerms.Any(t => Jaccard(t, sentence.Terms) >= RestatementSimilarity))
        {
            return false;
        }

        return GivesAnswer(sentence, question, questionTerms, focus);
    }

    // The passage holds the kind of answer the question asks for, beyond the question's own words.
    private static bool GivesAnswer(Sentence sentence, string question, HashSet<string> questionTerms, IReadOnlySet<string> focus)
    {
        var usable = sentence.Cells?.Where(c => c.Cell.Length > 0 && !Vague().IsMatch(c.Cell) && !Deferral().IsMatch(c.Cell)).ToList();
        var text = usable is not null ? string.Join("; ", usable.Select(c => c.Cell)) : sentence.Text;
        var newTerms = (usable is not null ? Terms(text) : sentence.Terms).Count(t => !questionTerms.Contains(t));
        if (focus.Count == 0 || focus.Contains("manner") || focus.Contains("place") || focus.Contains("reason"))
        {
            return newTerms >= 1;
        }

        if (focus.Contains("person"))
        {
            var askedRoles = Role().Matches(question).Select(m => m.Value.ToLowerInvariant()).ToHashSet();
            if (Role().Matches(text).Any(m => !askedRoles.Contains(m.Value.ToLowerInvariant()))
                || (usable?.Any(c => PersonHeader().IsMatch(c.Header) && Terms(c.Cell).Count > 0) ?? false))
            {
                return true;
            }
        }

        if (focus.Contains("time") && TimeExpression().IsMatch(text))
        {
            return true;
        }

        return focus.Contains("quantity") && Quantity().IsMatch(text);
    }

    private static List<Sentence> SplitSentences(string text)
    {
        var sentences = new List<Sentence>();
        var lines = SourceReferenceIndex.Lines(text);
        List<string>? header = null;
        var table = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                header = null;
                continue;
            }

            // A table: two or more consecutive rows of cells ("a | b | c", "| a | b |"); the first is its
            // header. The separator row of a Markdown table is skipped.
            if (TableCells(line) is { } cells && (header is not null || (i + 1 < lines.Length && TableCells(lines[i + 1].Trim()) is not null)))
            {
                if (cells.All(c => TableRule().IsMatch(c)))
                {
                    continue;
                }

                if (header is null)
                {
                    header = cells;
                    table++;
                    sentences.Add(new Sentence(i, line, Terms(line), true, table, Terms(line)));
                    continue;
                }

                var pairs = cells.Select((c, k) => (Header: k < header.Count ? header[k] : string.Empty, Cell: c)).ToList();
                var cellTerms = Terms(string.Join(" ", cells));
                var terms = Terms(string.Join(" ", header));
                terms.UnionWith(cellTerms);
                var read = string.Join("; ", pairs.Where(p => p.Cell.Length > 0).Select(p => p.Header.Length > 0 ? $"{p.Header}: {p.Cell}" : p.Cell));
                sentences.Add(new Sentence(i, read, terms, false, table, cellTerms, pairs));
                continue;
            }

            header = null;
            var heading = SourceChunker.IsSectionHeading(line);
            foreach (var part in SentenceBreak().Split(line))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    sentences.Add(new Sentence(i, trimmed, Terms(trimmed), heading));
                }
            }
        }

        return sentences;
    }

    // The cells of a table row written as "a | b" or "| a | b |" (two or more), else null.
    private static List<string>? TableCells(string line)
    {
        if (!line.Contains('|'))
        {
            return null;
        }

        var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
        return cells.Count >= 2 && cells.Count(c => c.Length > 0) >= 2 ? cells : null;
    }

    // ---- duplicates ------------------------------------------------------------------------------

    private static (string? Code, string Status)? DuplicateOf(
        Context context, ClarificationCandidate candidate, string? kind, HashSet<string> terms, IReadOnlyList<SourceReferencePayload> evidence)
    {
        var key = AiResultMerge.Key(candidate.Question);
        var topic = TopicKey(candidate.Topic);
        var lines = LinesOf(evidence);
        var places = context.SectionsOfLines(lines);
        var focus = QuestionFocus(candidate.Question);
        foreach (var known in context.KnownQuestions)
        {
            if (key.Length > 0 && key == known.Key)
            {
                return (known.Code, known.Status);
            }

            // "Who approves ...?" and "When is ... approved?" share their words but not their question.
            if (focus.Count > 0 && known.Focus.Count > 0 && !focus.Overlaps(known.Focus))
            {
                continue;
            }

            var similarity = Jaccard(terms, known.Terms);
            if (similarity >= DuplicateSimilarity
                || (topic is not null && topic == known.TopicKey && similarity >= TopicDuplicateSimilarity)
                || (lines.Count > 0 && lines.SetEquals(known.Lines) && kind is not null && kind == known.Kind && similarity >= SameEvidenceSimilarity)
                || (known.Legacy && Covers(known.ContextTerms, terms) && !(places.Count > 0 && known.Places.Count > 0 && !SamePlace(places, known.Places)))
                || (known.AnsweredTerms is not null && similarity >= SameEvidenceSimilarity && Covers(known.AnsweredTerms, terms)))
            {
                return (known.Code, known.Status);
            }
        }

        return null;
    }

    // At least two of the terms, and AnsweredCoverage of them, are in the other set.
    private static bool Covers(HashSet<string> of, HashSet<string> terms)
    {
        var shared = terms.Count(of.Contains);
        return shared >= 2 && shared >= AnsweredCoverage * terms.Count;
    }

    // Some section of one is, or contains, or lies in, a section of the other.
    private static bool SamePlace(IReadOnlyList<SourceSection> a, IReadOnlyList<SourceSection> b) =>
        a.Any(x => b.Any(y => (x.Line >= y.Line && x.EndLine <= y.EndLine) || (y.Line >= x.Line && y.EndLine <= x.EndLine)));

    // What a question asks for: a person ("who", "which role"), a time ("when", "how long", "how many
    // days"), a quantity ("how many", "what limit"), a place, a reason or a manner ("how"). Empty for
    // anything else ("what ...", "is ...") - which is compatible with every focus.
    public static IReadOnlySet<string> QuestionFocus(string? question)
    {
        var q = (question ?? string.Empty).Trim().ToLowerInvariant();
        var focus = new HashSet<string>(StringComparer.Ordinal);
        if (PersonFocus().IsMatch(q)) focus.Add("person");
        if (TimeFocus().IsMatch(q)) focus.Add("time");
        if (QuantityFocus().IsMatch(q)) focus.Add("quantity");
        if (Regex.IsMatch(q, @"\bwhere\b")) focus.Add("place");
        if (Regex.IsMatch(q, @"\bwhy\b")) focus.Add("reason");
        if (focus.Count == 0 && Regex.IsMatch(q, @"\bhow\b")) focus.Add("manner");
        return focus;
    }

    private static string? TopicKey(string? topic)
    {
        var key = AiResultMerge.Key(topic ?? string.Empty);
        return key.Length == 0 ? null : key;
    }

    // ---- terms -----------------------------------------------------------------------------------

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "if", "of", "to", "in", "on", "at", "by", "for", "with", "from", "as", "into",
        "about", "than", "then", "that", "this", "these", "those", "there", "their", "they", "them", "it", "its", "be", "been",
        "being", "is", "are", "was", "were", "am", "do", "does", "did", "done", "have", "has", "had", "having", "will", "would",
        "shall", "should", "must", "can", "could", "may", "might", "not", "no", "yes", "any", "all", "each", "every", "some",
        "such", "other", "own", "same", "so", "too", "very", "also", "only", "just", "what", "which", "who", "whom", "whose",
        "when", "where", "why", "how", "whether", "exactly", "specific", "specifically", "clarify", "clarification", "please",
        "define", "defined", "definition", "mean", "means", "meant", "system", "requirement", "requirements", "section",
        "document", "srs", "specification", "stated", "state", "states", "say", "says", "said", "expected", "expect", "need",
        "needs", "needed", "required", "require", "use", "used", "using", "via", "per", "up", "out", "over", "under", "within",
        "between", "after", "before", "more", "most", "less", "least", "one", "two", "both", "either", "neither", "our", "we",
        "you", "your", "he", "she", "his", "her", "i", "me", "my", "many", "much", "often"
    };

    private static readonly string[] Suffixes = ["ations", "ation", "ions", "ion", "ings", "ing", "ed", "es", "ly", "s", "al", "e"];

    // Stemmed words taken as one term: near-synonyms that requirement questions use interchangeably.
    // Kept short on purpose - each entry can make two questions read as one.
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["author"] = "approv", ["endors"] = "approv",                                  // authorise, authorize, endorse
        ["notify"] = "notifi", ["alert"] = "notifi",                                   // notify, notification, alert
        ["remov"] = "delet", ["eras"] = "delet", ["purg"] = "delet",                   // remove, erase, purge
        ["revok"] = "cancel", ["revoc"] = "cancel",                                    // revoke, revocation
        ["declin"] = "reject", ["deny"] = "reject", ["deni"] = "reject",               // decline, deny, denied
        ["modify"] = "chang", ["modifi"] = "chang", ["edit"] = "chang", ["updat"] = "chang", ["amend"] = "chang", ["amendm"] = "chang",
        ["maximu"] = "limit", ["max"] = "limit",                                       // maximum
        ["staff"] = "employ", ["worker"] = "employ",                                   // employee
        ["permit"] = "allow", ["permis"] = "allow",                                    // permit, permission
        ["retent"] = "retain", ["keep"] = "retain", ["kept"] = "retain",               // retention, keep
    };

    // Content words of a text, lightly stemmed (one suffix stripped, then at most 6 letters) so
    // "approve", "approval" and "approved" are one term, and near-synonyms mapped to one (Synonyms:
    // "authorises" is "approve"). Numbers are kept as written.
    public static HashSet<string> Terms(string? text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Word().Matches((text ?? string.Empty).ToLowerInvariant()))
        {
            var word = match.Value;
            if (word.Any(char.IsDigit))
            {
                terms.Add(word);
                continue;
            }

            if (word.Length < 3 || StopWords.Contains(word))
            {
                continue;
            }

            foreach (var suffix in Suffixes)
            {
                if (word.EndsWith(suffix, StringComparison.Ordinal) && word.Length - suffix.Length >= 3)
                {
                    word = word[..^suffix.Length];
                    break;
                }
            }

            var stem = word.Length > 6 ? word[..6] : word;
            terms.Add(Synonyms.GetValueOrDefault(stem, stem));
        }

        return terms;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var intersection = a.Count(b.Contains);
        return intersection / (double)(a.Count + b.Count - intersection);
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..157] + "...";

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[.,\-][\p{N}]+)*")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<=[.;!?])\s+")]
    private static partial Regex SentenceBreak();

    // A sentence that itself leaves the point open answers nothing.
    [GeneratedRegex(@"\b(?i:tbd|tbc|to be (?:decided|determined|confirmed|defined|agreed)|as required|as appropriate|as needed|as necessary|where applicable|if applicable|etc|and so on|unclear|not yet (?:decided|defined|known))\b")]
    private static partial Regex Vague();

    // A passage that sends the reader elsewhere instead of answering ("see the HR policy").
    [GeneratedRegex(@"\b(?i:see (?:also )?(?:the )?(?:sections?|appendix|appendices|annex|tables?|clauses?|chapters?|§|documents?|polic(?:y|ies)|attached|separate)|refer to|referred to|according to|in accordance with|as per|as (?:described|defined|specified|set out|listed|detailed|outlined|documented) (?:in|by)|(?:is|are) (?:described|defined|specified|set out|listed|detailed|outlined|documented|covered) (?:in|by))\b")]
    private static partial Regex Deferral();

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"\b(?:who|whom|whose)\b|\b(?:which|what) (?:roles?|users?|person|people|teams?|groups?|departments?|part(?:y|ies)|actors?|stakeholders?|approvers?|managers?|staff)\b")]
    private static partial Regex PersonFocus();

    [GeneratedRegex(@"^when\b|\bby when\b|\bdeadlines?\b|\bhow (?:long|soon|often|quickly|fast|frequently)\b|\b(?:what|which) (?:time|date|deadline|period|time ?frame|frequency|duration|response time)\b|\bhow many (?:seconds|minutes|hours|(?:working |business |calendar )?days|weeks|months|years)\b|\bwithin what\b")]
    private static partial Regex TimeFocus();

    [GeneratedRegex(@"\bhow (?:many|much)\b|\b(?:what|which) (?:is the )?(?:number|amount|limit|maximum|minimum|threshold|percentage|size|value|quota|cap)\b")]
    private static partial Regex QuantityFocus();

    [GeneratedRegex(@"\b(?i:managers?|supervisors?|directors?|administrators?|admins?|hr|human resources|heads? of|team leads?|leads?|officers?|owners?|approvers?|clerks?|receptionists?|committees?|boards?|employees?|staff|users?|customers?|clinicians?|nurses?|doctors?|accountants?|finance|payroll|auditors?|reviewers?|operators?|agents?|members?|departments?|requesters?|applicants?)\b")]
    private static partial Regex Role();

    [GeneratedRegex(@"\b(?i:approver|owner|responsible|role|actor|assignee|authori[sz]er|reviewer|contact|who|approved by|performed by|decision maker)\b")]
    private static partial Regex PersonHeader();

    [GeneratedRegex(@"(?i:\b(?:\d+(?:[.,]\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|twelve|fourteen|thirty)\s*(?:working |business |calendar )?(?:seconds?|secs?|minutes?|mins?|hours?|hrs?|days?|weeks?|months?|years?|ms)\b|\b(?:immediately|instantly|same day|next (?:working |business )?day|end of (?:the )?(?:day|week|month)|daily|weekly|monthly|quarterly|annually|yearly|real[- ]time)\b|\b\d{1,2}:\d{2}\b)")]
    private static partial Regex TimeExpression();

    [GeneratedRegex(@"\d|\b(?i:one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|twenty|thirty|hundred|thousand|unlimited|no (?:limit|maximum|cap))\b")]
    private static partial Regex Quantity();

    [GeneratedRegex(@"\b(?i:best practices?|industry standards?|any other|anything else|any (?:additional|further|more) (?:requirements?|features?|information|details?)|nice[- ]to[- ]have|future (?:enhancements?|releases?|versions?)|should we consider|do you have any|are there any (?:other|additional|further)|is there anything)\b")]
    private static partial Regex GenericQuestion();
}
