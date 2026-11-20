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

// One criterion of a candidate's quality assessment: Result is passed | failed | not-applicable, Detail
// what was found (the rejection message when it failed).
public sealed record QualityCheck(string Criterion, string Result, string Detail);

// A candidate rejected only as an unsupported assumption whose issue no other question covers, kept for
// a reviewer (ClarificationCandidateValidator.HeldForReview): the question as asked, its located evidence,
// the passage that denies its assumption (0-based line) and why it is held. Never rewritten or answered.
public sealed record HeldClarification(
    string Topic, string Question, string? Kind, IReadOnlyList<SourceReferencePayload> Evidence,
    int DenialLine, string DenialText, string Location, string Note);

// Outcome for one candidate: Accepted with its located evidence, or the reasons it was rejected.
// Assessment: what each check found, for traceability (never a substitute for the checks).
// DuplicateOfRun: the index of the verdict (in the same Validate run) it duplicates, if any.
public sealed record CandidateVerdict(
    ClarificationCandidate Candidate,
    string? Kind,
    IReadOnlyList<SourceReferencePayload> Evidence,
    IReadOnlyList<string> RequirementRefs,
    IReadOnlyList<CandidateRejection> Rejections,
    RuleRelation? Relation = null,
    IReadOnlyList<QualityCheck>? Assessment = null,
    int? DuplicateOfRun = null)
{
    public bool Accepted => Rejections.Count == 0;

    // The candidate's validation result (ClarificationOutcomes); "needs-review" is set by the caller for
    // a candidate it holds for review (ClarificationCandidateValidator.HeldForReview).
    public string Outcome => ClarificationOutcomes.Of(Rejections);
}

// The validation result of a candidate question, in the terms a reviewer uses:
//   valid             a genuine clarification need with evidence about it - the only result that is
//                     saved as an Open question;
//   duplicate         the same decision is already asked (in this run or before);
//   already-answered  the source, or an earlier answer, already gives the information;
//   unsupported       no verified passage of the source is about it, or what it rests on is not a
//                     genuine gap (rules that coexist, a generic or malformed question);
//   needs-review      held for a reviewer (HeldForReview): it may point at a genuine gap that could not
//                     be asked neutrally as written. Never shown as a confirmed question.
public static class ClarificationOutcomes
{
    public const string Valid = "valid";
    public const string Duplicate = "duplicate";
    public const string AlreadyAnswered = "already-answered";
    public const string Unsupported = "unsupported";
    public const string NeedsReview = "needs-review";

    public static string Of(IReadOnlyList<CandidateRejection> rejections)
    {
        if (rejections.Count == 0) return Valid;
        if (rejections.Any(r => r.Code is "answered-in-source" or "resolved-in-source" or "already-answered")) return AlreadyAnswered;
        if (rejections.Any(r => r.Code == "duplicate")) return Duplicate;
        return Unsupported;
    }
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
//                        of the whole source counts, so a one-line source can still be questioned. Only
//                        a question whose triggering passage is verified is ever shown: a question about
//                        missing detail must quote the requirement that lacks it;
//   ambiguous-location   every verified quote is written in several places and neither the sections the
//                        question cites nor its other evidence tell which one raised it
//                        (ClarificationEvidenceLocator) - no occurrence is guessed;
//   contradiction-needs-both-statements  a contradiction needs two verified quotes at different places;
//   cross-reference      a question that sets two passages against each other (a contradiction, or an
//                        ambiguity quoting two places) is checked as a business analyst would before
//                        raising it (RuleRelations, comparing the first two places):
//                          no-shared-subject    (contradiction) the two passages share no subject term;
//                          one-sided            (contradiction) the question concerns only one of them;
//                          different-conditions (contradiction) no case can meet both rules' conditions
//                                               ("Body Side = Left" / "Body Side = Bilateral"), or one
//                                               is the general rule and the other an exception to it
//                                               for a narrower case, stated as one in words or set by an
//                                               on/off setting ("If 'Is Careway' is disabled, ..."). The
//                                               conditions include what each rule inherits from the
//                                               document (RuleContextIndex: headings, list lead-ins,
//                                               scope sentences, table captions and headers), so rules
//                                               under "New referrals" and "Amendments" are different
//                                               workflows; a scope only one rule states is unknown;
//                          different-subjects   (contradiction) the shared words name different things
//                                               ("Referral notifications" / "Invoice notifications");
//                          same-rule            (contradiction) both passages state the same rule in
//                                               other words ("must be approved by the line manager" /
//                                               "the line manager authorises every leave request");
//                        A setting settles a general rule only when it is established to govern it (its
//                        name uses the rule's words, or a passage says it controls what the rule is
//                        about); an unrelated setting is no reason to drop the question;
//                          compatible-rules     (contradiction) both outcomes can hold together: neither
//                                               refuses or restricts anything and no number or value
//                                               differs - different behaviours are not a conflict;
//                          resolved-in-source   another passage states how the two relate (an exception,
//                                               a precedence or a scope naming something from each rule).
//                        Two rules that overlap without anything saying how they interact (one refuses
//                        an entity the other processes under an extra condition) are kept: that is the
//                        unresolved relationship the question must ask about;
//   unsupported-assumption  the question asks how/which/how many for something a passage of the source
//                        says is not supported, not allowed or out of scope, without acknowledging it
//                        ("How many API calls are made for a bilateral Wrist referral?" against "... is
//                        not supported by Careway") - it must ask how the passages relate instead;
//   unrelated            the question shares no content term with its own evidence or the headings of
//                        the sections that evidence sits in. Requirement IDs and numbers do not count: a
//                        shared "FR-012" or "3.2" does not show the question is about the passage;
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
//                        different things (QuestionFocus: "who" against "when"). Two questions that
//                        both set the same two (or more) passages against each other are one issue,
//                        whatever their wording. When both ask "who", role filler words ("responsible",
//                        "role", "person") are left out of the comparison, so "Who approves X?" and
//                        "Which role is responsible for authorising X?" are one question. Never a match
//                        when the two state different cases (DifferentConditions: different numbers,
//                        workflows, settings, or each a qualifier the other lacks - "for contractors" /
//                        "for employees"), or when only one states a real condition - a number, scope,
//                        setting, named party or a source term for a particular case ("Who approves
//                        expense claims?" does not cover "... over £500?"); padding the source never
//                        uses ("in the system") is not a condition.
// Every verdict carries an Assessment: per criterion what the checks found (a record, not a score).
// A candidate rejected only as an unsupported assumption, whose passages no other question covers, is
// held for review (HeldForReview) rather than lost; it is never rewritten or saved as a question.
// Terms: light stemming, a short list of synonyms (approve/authorise, disable/deactivate, mandatory/
// compulsory), multi-word phrases ("signed off" = approve, "turned off" = disable), entity names kept
// apart where the stem would merge them (employer/employee, clinician/clinic), and the terms the source
// itself says are the same (TermAliases: "Patient (also referred to as Client)").
// Limits: all similarity is lexical (light stemming and a short list of synonyms - approve/authorise,
// remove/delete, ...), so a paraphrase with other vocabulary can still pass the duplicate and
// answered-in-source checks, and a passage that matches the words and the kind of answer without really
// answering can reject a question - the rejection quotes the passage so a reviewer can see. Existing
// questions are only compared against, never changed.
public static partial class ClarificationCandidateValidator
{
    public const string Version = "CqValidation-v5";

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
            Aliases = TermAliases.From(SourceText);
            Sentences = SplitSentences(SourceText, Aliases);
            Lines = SourceReferenceIndex.Lines(SourceText);
            _document = new Lazy<RuleContextIndex>(() => RuleContextIndex.Build(SourceText));
            _frequency = new Lazy<Dictionary<string, int>>(() => Sentences.Where(s => !s.Heading)
                .SelectMany(s => s.Terms).GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
            NormalizedSource = SourceEvidence.Normalize(SourceText);
            _common = new Lazy<HashSet<string>>(() => EvidenceSupport.CommonTerms(Sentences.Where(s => !s.Heading).Select(s => s.Terms).ToList()));
            _decisions = new Lazy<List<DecisionBlock>>(() => DecisionBlocks(Lines));
            foreach (var e in existing)
            {
                var kind = NormalizeKind(e.Kind);
                var legacy = kind is null && e.EvidenceLines.Count == 0;
                var terms = TermsOf(e.Question);
                var context = TermsOf(e.Question);
                context.UnionWith(TermsOf(e.Topic));
                HashSet<string>? answered = null;
                if (ClosedStatuses.Contains(e.Status) && !string.IsNullOrWhiteSpace(e.Answer))
                {
                    answered = new HashSet<string>(context, StringComparer.Ordinal);
                    answered.UnionWith(TermsOf(e.Answer));
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
                    kind, e.Code, e.Status, context, answered, places, QuestionFocus(e.Question), legacy, Runs(e.EvidenceLines), e.Question));
            }
        }

        // Terms the source itself says are the same thing (TermAliases); none unless it says so.
        public TermAliases Aliases { get; }

        // A text's terms with the source's own aliases merged ("client" read as "patient" only when the
        // source states they are the same).
        public HashSet<string> TermsOf(string? text) => Aliases.Canon(Terms(text));

        // In how many sentences of the source (headings left out) a term occurs.
        private readonly Lazy<Dictionary<string, int>> _frequency;

        internal int Frequency(string term) => _frequency.Value.GetValueOrDefault(term);

        // A term few sentences use, so it names something specific ("wrist") rather than the document's
        // general subject ("referral", "Careway").
        internal bool Specific(string term) => Frequency(term) <= Math.Max(2, Sentences.Count / 20);

        // The terms the document uses throughout (EvidenceSupport): sharing only these with a passage
        // does not make it evidence for a question.
        private readonly Lazy<HashSet<string>> _common;

        public IReadOnlySet<string> CommonTerms => _common.Value;

        // Places where the source lists options for one open decision (DecisionBlocks).
        private readonly Lazy<List<DecisionBlock>> _decisions;

        internal IReadOnlyList<DecisionBlock> Decisions => _decisions.Value;

        public string SourceText { get; }
        public SourceReferenceIndex References { get; }
        public SourceEvidenceIndex Evidence { get; }
        public ClarificationEvidenceLocator Locator { get; }
        internal IReadOnlyList<Sentence> Sentences { get; }
        internal string[] Lines { get; }
        internal string NormalizedSource { get; }

        // The document structure rules inherit context from; built only when a question is cross-referenced.
        private readonly Lazy<RuleContextIndex> _document;
        public RuleContextIndex Document => _document.Value;

        internal IEnumerable<Known> KnownQuestions => _known;

        // How many candidates this context has validated (Validate): the next run index.
        internal int RunCounter { get; set; }

        internal void Remember(ClarificationCandidate candidate, string? kind, IEnumerable<SourceReferencePayload> evidence, int runIndex = -1)
        {
            var lines = LinesOf(evidence);
            var context = TermsOf(candidate.Question);
            context.UnionWith(TermsOf(candidate.Topic));
            _known.Add(new Known(AiResultMerge.Key(candidate.Question), TermsOf(candidate.Question), TopicKey(candidate.Topic),
                lines, kind, null, "Accepted in this run", context, null, SectionsOfLines(lines), QuestionFocus(candidate.Question), false, Runs(lines),
                candidate.Question, runIndex));
        }

        // A question accepted in this run took over a duplicate's passages: later candidates are compared
        // with all of them.
        internal void Extend(int runIndex, IEnumerable<SourceReferencePayload> evidence)
        {
            var i = _known.FindIndex(k => k.RunIndex == runIndex);
            if (i < 0)
            {
                return;
            }

            var lines = new HashSet<int>(_known[i].Lines);
            lines.UnionWith(LinesOf(evidence));
            _known[i] = _known[i] with { Lines = lines, Places = SectionsOfLines(lines), Passages = Runs(lines) };
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
    // sections it is located in (empty when not known); Legacy: saved before v3, without kind or evidence;
    // Passages: its evidence lines as separate passages (runs of consecutive lines); Question: as written,
    // for the condition comparison (QuestionConditions); RunIndex: its verdict's index when it was
    // accepted in this run, else -1.
    internal sealed record Known(
        string Key, HashSet<string> Terms, string? TopicKey, HashSet<int> Lines, string? Kind, string? Code, string Status,
        HashSet<string> ContextTerms, HashSet<string>? AnsweredTerms, IReadOnlyList<SourceSection> Places,
        IReadOnlySet<string> Focus, bool Legacy, IReadOnlyList<HashSet<int>> Passages, string Question, int RunIndex = -1);

    // Where the source lists options for one open decision: the line that raises it (when it reads as
    // one - "need consideration as to how ...", "Unsure how this will be done"), each "Option 1:",
    // "Option 2:" (or Alternative / Approach) line and the lines that continue the last option, up to a
    // new point ("Consider how ...", "Note: ...", a numbered step that starts another consideration) or
    // the end of the section. Start inclusive, End exclusive, 0-based lines.
    internal sealed record DecisionBlock(int Start, int End)
    {
        public bool Contains(int line) => line >= Start && line < End;
    }

    internal const int MaxOptionContinuation = 20;

    internal static List<DecisionBlock> DecisionBlocks(string[] lines)
    {
        var blocks = new List<DecisionBlock>();
        var i = 0;
        while (i < lines.Length)
        {
            if (!OptionLine().IsMatch(lines[i]))
            {
                i++;
                continue;
            }

            var first = i;
            var last = i;
            var end = i + 1;
            for (var k = i + 1; k < lines.Length && k - last <= MaxOptionContinuation; k++)
            {
                var line = lines[k].Trim();
                if (OptionLine().IsMatch(line))
                {
                    last = k;
                    end = k + 1;
                    continue;
                }

                if (NewPoint().IsMatch(line) || SourceChunker.IsSectionHeading(line) && !line.EndsWith(':') && line.Length > 0 && !TemplateLine().IsMatch(line))
                {
                    break;
                }

                end = k + 1;
            }

            if (last > first)
            {
                var start = first;
                var previous = first - 1;
                while (previous >= 0 && lines[previous].Trim().Length == 0) previous--;
                if (previous >= 0 && DecisionIntro().IsMatch(lines[previous]))
                {
                    start = previous;
                }

                while (end > last + 1 && lines[end - 1].Trim().Length == 0) end--;
                blocks.Add(new DecisionBlock(start, end));
            }

            i = Math.Max(end, i + 1);
        }

        return blocks;
    }

    // "i)\tOption 1: ...", "Option B -", "Alternative 2.", "Approach A:".
    [GeneratedRegex(@"^\s*(?:[\(\[]?(?:\d{1,3}|[a-z]|[ivx]{1,4})[\.\)\]]\s*)?(?i:option|alternative|approach)\s+(?:\d{1,2}|[a-zA-Z])\b\s*[:.\-\u2013\u2014)]")]
    private static partial Regex OptionLine();

    // A line that starts another point after a list of options.
    [GeneratedRegex(@"^\s*(?:[\(\[]?(?:\d{1,3}|[a-z]|[ivx]{1,4})[\.\)\]]\s*)?(?i:consider\b|consideration\b|note\b|nb\b|special case\b|process flow\b|business rules?\b)")]
    private static partial Regex NewPoint();

    // A placeholder line of a template or example inside an option ("#Date#", "Cc: ...", "e.g.").
    [GeneratedRegex(@"^\s*(?:#[^#]+#|(?i:cc|re|e\.g\.|dear)\b)")]
    private static partial Regex TemplateLine();

    // The line before a list of options that raises the decision.
    [GeneratedRegex(@"(?i:\bconsider\w*|\bdecide\b|\bdecision\b|\bhow\b|\bwhether\b|\boptions?\b|\bunsure\b|\bundecided\b|\?)")]
    private static partial Regex DecisionIntro();

    // Lines grouped into passages: each run of consecutive line numbers is one.
    internal static List<HashSet<int>> Runs(IEnumerable<int> lines)
    {
        var runs = new List<HashSet<int>>();
        var previous = int.MinValue;
        foreach (var line in lines.Where(l => l >= 0).Distinct().Order())
        {
            if (runs.Count == 0 || line != previous + 1)
            {
                runs.Add([]);
            }

            runs[^1].Add(line);
            previous = line;
        }

        return runs;
    }

    // Validates candidates in order; an accepted one counts for the duplicate check of later ones. A
    // candidate rejected only as a duplicate of one accepted earlier in the run hands that question its
    // own passages that support it too (EvidenceSupport), up to MaxEvidenceQuotes, so the question kept
    // shows all the evidence for the decision; nothing else of the duplicate is kept.
    public static List<CandidateVerdict> Validate(Context context, IEnumerable<ClarificationCandidate> candidates)
    {
        // Run indexes are unique per context, so a context validated more than once never mixes runs.
        var start = context.RunCounter;
        var verdicts = new List<CandidateVerdict>();
        foreach (var candidate in candidates)
        {
            var verdict = ValidateOne(context, candidate);
            if (verdict.Accepted)
            {
                context.Remember(candidate, verdict.Kind, verdict.Evidence, start + verdicts.Count);
            }
            else if (verdict.DuplicateOfRun is int run && run >= start && run - start < verdicts.Count
                     && verdict.Rejections.All(r => r.Code == "duplicate")
                     && MergeInto(context, run, verdicts[run - start], verdict) is { } merged)
            {
                verdicts[run - start] = merged.Kept;
                verdict = verdict with { Rejections = merged.Rejections };
            }

            verdicts.Add(verdict);
            context.RunCounter = start + verdicts.Count;
        }

        return verdicts;
    }

    // The kept question with the duplicate's supporting passages added, and the duplicate's rejection
    // saying so; null when none of its passages adds anything (or the kept one is a contradiction, whose
    // evidence is exactly its two conflicting statements).
    private static (CandidateVerdict Kept, IReadOnlyList<CandidateRejection> Rejections)? MergeInto(
        Context context, int keptIndex, CandidateVerdict kept, CandidateVerdict duplicate)
    {
        if (kept.Kind == "contradiction")
        {
            return null;
        }

        var keptTerms = context.TermsOf(kept.Candidate.Question);
        var lines = LinesOf(kept.Evidence);
        var added = new List<SourceReferencePayload>();
        foreach (var e in duplicate.Evidence.Where(e => e.Line >= 0))
        {
            if (kept.Evidence.Count + added.Count >= MaxEvidenceQuotes)
            {
                break;
            }

            var passage = Enumerable.Range(e.Line, Math.Max(e.EndLine - e.Line, 1));
            if (!passage.Any(lines.Contains) && EvidenceSupport.Assess(keptTerms, context.TermsOf(e.Excerpt), context.CommonTerms).Supports)
            {
                added.Add(e);
                lines.UnionWith(passage);
            }
        }

        if (added.Count == 0)
        {
            return null;
        }

        var evidence = kept.Evidence.Concat(added).ToList();
        context.Extend(keptIndex, added);
        var rejections = duplicate.Rejections
            .Select(r => r.Code == "duplicate"
                ? r with { Message = $"{r.Message} Its {added.Count} supporting passage(s) ({string.Join(", ", added.Select(a => $"line {a.Line + 1}"))}) were added to that question." }
                : r)
            .ToList();
        return (kept with { Evidence = evidence, RequirementRefs = RequirementRefsOf(evidence) }, rejections);
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

        var (evidence, ambiguous) = LocateEvidence(context, candidate);
        var questionTerms = context.TermsOf(question);

        // Each passage must itself be about the question (EvidenceSupport); one that shares only the
        // document's general vocabulary is not attached. When none is, the question has no evidence.
        var (supporting, unsupported) = SplitBySupport(context, questionTerms, evidence, kind);
        var irrelevant = evidence.Count > 0 && supporting.Count == 0 && Relevant(questionTerms, evidence, question);
        if (supporting.Count > 0)
        {
            evidence = supporting;
        }

        if (irrelevant)
        {
            var words = evidence.SelectMany(e => EvidenceSupport.Assess(questionTerms, context.TermsOf(e.Excerpt), context.CommonTerms).Shared)
                .Distinct().Take(4).ToList();
            rejections.Add(new("irrelevant-evidence",
                $"None of the cited passages is about what the question asks: they share only words the document uses throughout ({string.Join(", ", words)})."));
        }

        // A question that names what it is about in words the long document uses only in a few places
        // ("ARTP", "logged in", "administrator") must cite at least one passage that says them; one that
        // cites only passages about something else - a referral letter for a question about the ARTP -
        // took its evidence from the wrong place.
        if (!irrelevant && supporting.Count > 0 && MissedSubject(context, question, questionTerms, evidence) is { } missed)
        {
            rejections.Add(new("evidence-misses-subject",
                $"The question is about {string.Join(", ", missed.Select(m => $"\"{m}\""))}, which the source writes elsewhere, but none of its passages mentions them."));
        }

        if (evidence.Count == 0 && ambiguous > 0)
        {
            rejections.Add(new("ambiguous-location",
                "The evidence is written in several places in the source and nothing in the question tells which one raised it."));
        }
        else if (evidence.Count == 0)
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

        if (evidence.Count > 0 && !irrelevant && !Relevant(questionTerms, evidence, question))
        {
            rejections.Add(new("unrelated", "The question shares no content with the evidence it cites."));
        }

        RuleRelation? relation = null;
        if (kind is "contradiction" or "ambiguous" && DistinctPlaces(evidence) >= 2)
        {
            relation = CrossReference(context, candidate, kind, questionTerms, evidence, rejections);
        }

        if (question.Length > 0 && UnsupportedAssumption(context, question, questionTerms) is { } denial)
        {
            var section = context.SectionsOfLines([denial.Line]).FirstOrDefault();
            var location = section is not null ? $"{section.HeadingLine.TrimStart('#', ' ')} (line {denial.Line + 1})" : $"line {denial.Line + 1}";
            rejections.Add(new("unsupported-assumption",
                $"The question takes for granted something the source denies at {location}: \"{Shorten(denial.Text)}\". " +
                "Ask how the passages relate instead of assuming either one.", Location: location));
        }

        (string Location, string Text)? answer = null;
        if (evidence.Count > 0 && kind is not "contradiction" && AnsweredElsewhere(context, candidate, questionTerms, evidence) is { } found)
        {
            answer = found;
            rejections.Add(new("answered-in-source",
                $"The source already states this at {found.Location}: \"{Shorten(found.Text)}\"", Location: found.Location));
        }

        int? duplicateRun = null;
        if (question.Length > 0 && DuplicateOf(context, candidate, kind, questionTerms, evidence) is { } duplicate)
        {
            var closed = ClosedStatuses.Contains(duplicate.Status);
            var name = duplicate.Code ?? $"a question accepted earlier in this run (\"{Shorten(duplicate.Question)}\")";
            rejections.Add(closed
                ? new("already-answered", $"Already asked and {duplicate.Status.ToLowerInvariant()} as {name}.", duplicate.Code)
                : new("duplicate", $"Duplicate of {name}.", duplicate.Code));
            duplicateRun = duplicate.RunIndex >= 0 ? duplicate.RunIndex : null;
        }

        return new CandidateVerdict(candidate, kind, evidence, RequirementRefsOf(evidence), rejections, relation,
            Assess(context, candidate, kind, evidence, relation, rejections, unsupported), duplicateRun);
    }

    // A term used in at most this many sentences of a long document names something particular in it.
    internal const int RareFrequency = 5;

    // The question's rare terms (see the evidence-misses-subject check) when it has at least two, the
    // document is long (EvidenceSupport.MinSentences) and no passage (nor its section heading) holds any
    // of them; else null. Each is shown as the question writes it.
    private static List<string>? MissedSubject(Context context, string question, HashSet<string> questionTerms, IReadOnlyList<SourceReferencePayload> evidence)
    {
        if (context.Sentences.Count(s => !s.Heading) < EvidenceSupport.MinSentences)
        {
            return null;
        }

        var rare = questionTerms.Where(t => !t.Any(char.IsDigit) && context.Frequency(t) is >= 1 and <= RareFrequency).ToList();
        if (rare.Count < 2)
        {
            return null;
        }

        var cited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in evidence)
        {
            // The whole lines the passage is on: a quote may be part of a sentence whose other words say
            // what the question is about.
            cited.UnionWith(context.TermsOf(e.Line >= 0 ? PassageText(context, e) : e.Excerpt));
            cited.UnionWith(context.TermsOf(e.Heading?.TrimStart('#', ' ')));
        }

        return rare.Any(cited.Contains) ? null : rare
            .Select(t => Word().Matches(question).Select(m => m.Value).FirstOrDefault(w => context.TermsOf(w).Contains(t)) ?? t).ToList();
    }

    // The located passages that support the question (EvidenceSupport) and those that do not. For a
    // question that sets passages against each other (a contradiction, or an ambiguity between passages)
    // a passage is also kept when it is the other rule: it shares a term that is not common with a
    // passage that supports the question ("cancel up to 24 hours ..." / "can be cancelled up to 48
    // hours ..." for "Which window governs late withdrawals of leave?"). Whether the rules really
    // relate is then the cross-reference check's to decide.
    private static (List<SourceReferencePayload> Supporting, List<SourceReferencePayload> Unsupported) SplitBySupport(
        Context context, HashSet<string> questionTerms, IReadOnlyList<SourceReferencePayload> evidence, string? kind)
    {
        var supporting = new List<SourceReferencePayload>();
        var unsupported = new List<SourceReferencePayload>();
        foreach (var e in evidence)
        {
            (EvidenceSupport.Assess(questionTerms, context.TermsOf(e.Excerpt), context.CommonTerms).Supports ? supporting : unsupported).Add(e);
        }

        if (kind is "contradiction" or "ambiguous" && supporting.Count > 0 && unsupported.Count > 0)
        {
            var anchor = supporting.SelectMany(e => context.TermsOf(e.Excerpt)).Where(t => !context.CommonTerms.Contains(t) && !t.Any(char.IsDigit))
                .ToHashSet(StringComparer.Ordinal);
            var counterparts = unsupported.Where(e => context.TermsOf(e.Excerpt).Overlaps(anchor)).ToList();
            supporting = evidence.Where(e => supporting.Contains(e) || counterparts.Contains(e)).ToList();
            unsupported = unsupported.Except(counterparts).ToList();
        }

        return (supporting, unsupported);
    }

    // ---- quality assessment ----------------------------------------------------------------------

    // What each check found, criterion by criterion - a record of the deterministic checks above, not a
    // score: a candidate is accepted only when none of them failed, whatever this says.
    private static List<QualityCheck> Assess(
        Context context, ClarificationCandidate candidate, string? kind, IReadOnlyList<SourceReferencePayload> evidence,
        RuleRelation? relation, List<CandidateRejection> rejections, IReadOnlyList<SourceReferencePayload>? unsupported = null)
    {
        string[] Failed(params string[] codes) => rejections.Where(r => codes.Contains(r.Code)).Select(r => $"{r.Code}: {r.Message}").ToArray();
        QualityCheck Check(string criterion, string passed, params string[] codes)
        {
            var failed = Failed(codes);
            return new QualityCheck(criterion, failed.Length > 0 ? "failed" : "passed", failed.Length > 0 ? string.Join(" ", failed) : passed);
        }

        var located = evidence.Where(e => e.Line >= 0).Select(e => $"line {e.Line + 1}").Distinct().ToList();
        var dropped = (unsupported ?? []).Where(e => e.Line >= 0).Select(e => $"line {e.Line + 1}").Distinct().ToList();
        var checks = new List<QualityCheck>
        {
            Check("question-form", "One specific question of a known kind, with a reason and an impact.",
                "missing-fields", "not-a-question", "multiple-questions", "invalid-kind", "generic", "unrelated"),
            Check("source-evidence", $"{located.Count} passage(s) found verbatim in the source ({string.Join(", ", located)}), each about what the question asks" +
                    (dropped.Count > 0 ? $"; not attached, as they share only the document's general vocabulary with the question: {string.Join(", ", dropped)}." : "."),
                "no-evidence", "ambiguous-location", "contradiction-needs-both-statements", "irrelevant-evidence"),
            Check("business-impact", "The impact on requirements, design, implementation or acceptance is stated.", "missing-fields"),
            kind == "contradiction"
                ? new QualityCheck("information-gap", "not-applicable", "A contradiction is checked by cross-referencing its passages, not by searching for an answer.")
                : Check("information-gap", "No other passage of the source answers it.", "answered-in-source"),
            Check("unsupported-assumption", "Nothing it takes for granted is denied by the source.", "unsupported-assumption"),
            Check("duplicate", "No existing or earlier question asks about the same issue under the same conditions.", "duplicate", "already-answered")
        };

        if (relation is null)
        {
            checks.Add(new QualityCheck("cross-reference", "not-applicable", "The question does not set two passages against each other."));
            checks.Add(new QualityCheck("condition-clarity", "not-applicable", "No rule comparison was needed."));
            return checks;
        }

        string Conditions(RuleStatement r) =>
            $"line {r.Line + 1}: condition {(r.Condition.Trim().Length > 0 ? $"\"{Shorten(r.Condition.Trim())}\"" : "none written")}" +
            (r.Scope.Count > 0 ? $", scope {string.Join(", ", r.Scope.Values.Select(s => $"{s.Dimension}={s.Value} ({s.From})"))}" : ", scope unknown") +
            (r.Context.Count > 0 ? $", {r.Context.Count} inherited context item(s)" : string.Empty);
        checks.Add(Check("condition-clarity", $"{Conditions(relation.First)}; {Conditions(relation.Second)}.", "different-conditions"));
        checks.Add(Check("cross-reference",
            relation.Conflict is { } conflict ? $"The rules overlap and clash ({conflict}); nothing in the source says how they relate." : "Kept as an ambiguity between the passages.",
            "no-shared-subject", "one-sided", "different-subjects", "same-rule", "compatible-rules", "resolved-in-source"));
        return checks;
    }

    // ---- assumptions -------------------------------------------------------------------------------

    // A question that asks how to carry something out ("How many API calls are made for a bilateral Wrist
    // referral?") while a passage of the source says that thing is not supported, not allowed or out of
    // scope ("... the injury type is not supported by Careway") takes an unconfirmed business decision for
    // granted. Only a passage that applies without a setting or scope of its own counts (one that holds
    // only "If 'Is Careway' is disabled" says nothing about other cases), and the question must name
    // something specific to it - at least two of its terms, one of them used by few sentences of the
    // source, so the document's general subject ("referral", "Careway") is not enough. A question that
    // acknowledges the restriction ("not supported", "whether", "rejected", "which rule") or asks yes/no
    // assumes nothing.
    private static (int Line, string Text)? UnsupportedAssumption(Context context, string question, HashSet<string> questionTerms)
    {
        if (!Presupposing().IsMatch(question) || Acknowledges().IsMatch(question))
        {
            return null;
        }

        foreach (var sentence in context.Sentences.Where(s => !s.Heading && s.Table < 0 && Denial().IsMatch(s.Text)))
        {
            var rule = RuleRelations.Read(sentence.Text);
            if (rule.ConditionStates.Count > 0 || RuleRelations.ScopesOf(rule.Condition).Count > 0)
            {
                continue;
            }

            var shared = sentence.Terms.Where(t => questionTerms.Contains(t) && !t.Any(char.IsDigit)).ToList();
            if (shared.Count >= 2 && shared.Any(context.Specific))
            {
                return (sentence.Line, sentence.Text);
            }
        }

        return null;
    }

    // A candidate turned down only for taking for granted what the source denies may still point at a
    // genuine gap - whether its passage applies to the case the other passage excludes. When no accepted
    // or existing question takes that up (none cites the denying passage), it is held for a reviewer,
    // as asked and with both passages, instead of disappearing among the rejections. Nothing is rewritten
    // or answered: the record says only which passages meet and that their relation is unstated. Call
    // after Validate, whose accepted questions the context remembers.
    public static List<HeldClarification> HeldForReview(Context context, IEnumerable<CandidateVerdict> verdicts)
    {
        var held = new List<HeldClarification>();
        foreach (var verdict in verdicts)
        {
            if (HeldConflict(context, verdict, held) is { } conflict)
            {
                held.Add(conflict);
                continue;
            }

            if (verdict.Rejections.Count == 0 || verdict.Rejections.Any(r => r.Code != "unsupported-assumption"))
            {
                continue;
            }

            var question = verdict.Candidate.Question.Trim();
            if (UnsupportedAssumption(context, question, context.TermsOf(question)) is not { } denial
                || context.KnownQuestions.Any(k => k.Lines.Contains(denial.Line))
                || held.Any(h => h.DenialLine == denial.Line && LinesOf(h.Evidence).SetEquals(LinesOf(verdict.Evidence))))
            {
                continue;
            }

            var location = verdict.Rejections[0].Location ?? $"line {denial.Line + 1}";
            var cited = string.Join(", ", verdict.Evidence.Where(e => e.Line >= 0).Select(e => $"line {e.Line + 1}").Distinct());
            held.Add(new HeldClarification(
                verdict.Candidate.Topic, question, verdict.Kind, verdict.Evidence, denial.Line, denial.Text, location,
                $"Not asked as written: it assumes something the source denies at {location}. No other question covers how " +
                $"that passage relates to the cited evidence ({(cited.Length > 0 ? cited : "unlocated")}); a reviewer should " +
                "decide whether to ask it neutrally."));
        }

        return held;
    }

    // A contradiction whose passages genuinely clash - the cross-reference found a conflict, no condition
    // that keeps the rules apart, no passage settling them, not the same rule, not different subjects -
    // but which was rejected only for how it is asked (one-sided, or assuming one rule wins), is held
    // rather than lost when no other question already sets the same two passages against each other.
    // DenialLine/DenialText: the second conflicting passage.
    private static HeldClarification? HeldConflict(Context context, CandidateVerdict verdict, List<HeldClarification> held)
    {
        if (verdict.Kind != "contradiction" || verdict.Relation is not { } relation || verdict.Rejections.Count == 0
            || verdict.Rejections.Any(r => r.Code is not ("one-sided" or "unsupported-assumption"))
            || relation.Conflict is null || relation.ExclusiveConditions is not null || relation.Resolution is not null
            || relation.SameRule is not null || relation.DifferentSubjects is not null || relation.SharedSubject.Count == 0)
        {
            return null;
        }

        var located = verdict.Evidence.Where(e => e.Line >= 0).ToList();
        var first = located.FirstOrDefault();
        var second = located.FirstOrDefault(e => first is not null && !(e.Line < first.EndLine && first.Line < e.EndLine));
        if (first is null || second is null)
        {
            return null;
        }

        bool Cites(HashSet<int> lines, SourceReferencePayload e) => Enumerable.Range(e.Line, Math.Max(e.EndLine - e.Line, 1)).Any(lines.Contains);
        if (context.KnownQuestions.Any(k => Cites(k.Lines, first) && Cites(k.Lines, second))
            || held.Any(h => h.DenialLine == second.Line && LinesOf(h.Evidence).Contains(first.Line)))
        {
            return null;
        }

        var section = context.SectionsOfLines([second.Line]).FirstOrDefault();
        var location = section is not null ? $"{section.HeadingLine.TrimStart('#', ' ')} (line {second.Line + 1})" : $"line {second.Line + 1}";
        return new HeldClarification(
            verdict.Candidate.Topic, verdict.Candidate.Question.Trim(), verdict.Kind, verdict.Evidence, second.Line, second.Excerpt ?? string.Empty, location,
            $"The passages at line {first.Line + 1} and {location} genuinely conflict ({relation.Conflict}), and nothing in the source " +
            "settles it, but the question as written is not neutral (it concerns one passage or assumes which rule applies). " +
            "A reviewer should ask it neutrally, presenting both passages.");
    }

    // ---- cross-reference -------------------------------------------------------------------------

    // The first two passages at different places, compared as rules (see the class comment). The
    // passages are read from the source's own lines, so a condition written earlier in the line than the
    // quote is seen; every other sentence of the source may resolve them.
    private static RuleRelation CrossReference(
        Context context, ClarificationCandidate candidate, string kind, HashSet<string> questionTerms,
        IReadOnlyList<SourceReferencePayload> evidence, List<CandidateRejection> rejections)
    {
        var first = evidence.First(e => e.Line >= 0);
        var second = evidence.First(e => e.Line >= 0 && !(e.Line < first.EndLine && first.Line < e.EndLine));
        var a = RuleRelations.Read(PassageText(context, first), first.Line, first.Excerpt, context.Document);
        var b = RuleRelations.Read(PassageText(context, second), second.Line, second.Excerpt, context.Document);
        var cited = LinesOf(evidence);
        var relation = RuleRelations.Compare(a, b, context.Sentences
            .Where(s => !s.Heading && s.Table < 0 && !cited.Contains(s.Line))
            .Select(s => (s.Line, s.Text)), context.Aliases);

        var sameSection = first.Heading is { Length: > 0 } && first.Heading == second.Heading;
        string Place(SourceReferencePayload e) => e.Heading is { Length: > 0 } h
            ? sameSection ? $"\"{h.TrimStart('#', ' ')}\" line {e.Line + 1}" : $"\"{h.TrimStart('#', ' ')}\""
            : $"line {e.Line + 1}";

        if (kind == "contradiction")
        {
            if (relation.SharedSubject.Count == 0)
            {
                rejections.Add(new("no-shared-subject",
                    $"The passages at {Place(first)} and {Place(second)} share no subject, so they cannot contradict each other."));
            }
            else
            {
                var aTerms = context.TermsOf(a.Text);
                var bTerms = context.TermsOf(b.Text);
                var shared = aTerms.Where(bTerms.Contains).ToHashSet(StringComparer.Ordinal);
                if (!questionTerms.Overlaps(shared) && !(questionTerms.Overlaps(aTerms) && questionTerms.Overlaps(bTerms)))
                {
                    rejections.Add(new("one-sided",
                        "The question concerns only one of the two passages it cites as conflicting; it must ask how they relate."));
                }
            }

            if (relation.SharedSubject.Count > 0 && relation.DifferentSubjects is { } subjects)
            {
                rejections.Add(new("different-subjects",
                    $"The passages use the same words for different things ({subjects}), so they do not contradict each other."));
            }

            if (relation.ExclusiveConditions is { } exclusive)
            {
                rejections.Add(new("different-conditions",
                    $"Both rules can hold: they apply under different conditions ({exclusive})."));
            }

            if (relation.SameRule is { } same)
            {
                rejections.Add(new("same-rule",
                    $"The passages at {Place(first)} and {Place(second)} state the same rule in different words ({same}), so they do not contradict each other."));
            }
            else if (relation.SharedSubject.Count > 0 && relation.Conflict is null)
            {
                rejections.Add(new("compatible-rules",
                    $"The passages at {Place(first)} and {Place(second)} describe different behaviours that can both apply " +
                    $"(about {string.Join(", ", relation.SharedSubject.Take(4))}); neither refuses or restricts what the other does."));
            }
        }

        if (relation.Resolution is { } resolution)
        {
            var section = context.SectionsOfLines([resolution.Line]).FirstOrDefault();
            var location = section is not null ? $"{section.HeadingLine.TrimStart('#', ' ')} (line {resolution.Line + 1})" : $"line {resolution.Line + 1}";
            rejections.Add(new("resolved-in-source",
                $"The source states how the two rules relate at {location}: \"{Shorten(resolution.Text)}\"", Location: location));
        }

        return relation;
    }

    private static string PassageText(Context context, SourceReferencePayload e) =>
        string.Join(" ", context.Lines.Skip(e.Line).Take(Math.Max(e.EndLine - e.Line, 1)).Select(l => l.Trim()));

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
    private static (List<SourceReferencePayload> Evidence, int Ambiguous) LocateEvidence(Context context, ClarificationCandidate candidate)
    {
        var located = context.Locator.LocateAll(
            candidate.EvidenceQuotes.Take(MaxEvidenceQuotes),
            context.References.Resolve(candidate.Reason, candidate.SourceRefs));
        return (located.Places, located.Ambiguous);
    }

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

    // Requirement IDs, section numbers and other numbers are left out of both sides.
    public static bool Relevant(string? question, IReadOnlyList<SourceReferencePayload> evidence) =>
        Relevant(Terms(question), evidence, question);

    private static bool Relevant(HashSet<string> questionTerms, IReadOnlyList<SourceReferencePayload> evidence, string? question = null)
    {
        if (RelevantTerms(questionTerms, evidence, semantic: true))
        {
            return true;
        }

        // Read the old way too (no phrases, no entity names), so the newer reading never makes a stored
        // question lose the overlap with its evidence it had before.
        return question is not null && RelevantTerms(Terms(question, semantic: false), evidence, semantic: false);
    }

    private static bool RelevantTerms(HashSet<string> questionTerms, IReadOnlyList<SourceReferencePayload> evidence, bool semantic)
    {
        var words = questionTerms.Where(t => !t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        if (words.Count == 0)
        {
            return false;
        }

        var context = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in evidence)
        {
            context.UnionWith(Terms(e.Excerpt, semantic));
            context.UnionWith(Terms(e.Heading?.TrimStart('#', ' '), semantic));
        }

        return words.Overlaps(context);
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
        var evidenceTerms = evidence.Select(e => context.TermsOf(e.Excerpt)).Where(t => t.Count > 0).ToList();
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

    private static List<Sentence> SplitSentences(string text, TermAliases aliases)
    {
        HashSet<string> Terms(string t) => aliases.Canon(ClarificationCandidateValidator.Terms(t));
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

    private static Known? DuplicateOf(
        Context context, ClarificationCandidate candidate, string? kind, HashSet<string> terms, IReadOnlyList<SourceReferencePayload> evidence)
    {
        var key = AiResultMerge.Key(candidate.Question);
        var topic = TopicKey(candidate.Topic);
        var lines = LinesOf(evidence);
        var places = context.SectionsOfLines(lines);
        var passages = Runs(lines);
        var focus = QuestionFocus(candidate.Question);
        foreach (var known in context.KnownQuestions)
        {
            if (key.Length > 0 && key == known.Key)
            {
                return known;
            }

            // "Who approves ...?" and "When is ... approved?" share their words but not their question.
            if (focus.Count > 0 && known.Focus.Count > 0 && !focus.Overlaps(known.Focus))
            {
                continue;
            }

            // Both ask "who": "responsible", "role", "person" add nothing to tell them apart.
            var person = focus.Contains("person") && known.Focus.Contains("person");
            var (mine, theirs) = person ? (WithoutRoleFillers(terms), WithoutRoleFillers(known.Terms)) : (terms, known.Terms);

            // Different numbers, workflows or settings: different cases, however alike the words.
            if (DifferentConditions(context, candidate.Question, known.Question, mine, theirs, strongOnly: true) is not null)
            {
                continue;
            }

            // Both rest on the options the source lists for one open decision ("How should the IDT
            // physiotherapist be assigned?" / "Should a second referral letter or the Cc field be used?"):
            // whichever option is asked about, the stakeholder makes one decision.
            if (lines.Count > 0 && known.Lines.Count > 0
                && context.Decisions.Any(d => lines.Any(d.Contains) && known.Lines.Any(d.Contains)))
            {
                return known;
            }

            // The same two (or more) passages set against each other: one issue, however it is worded.
            if (passages.Count >= 2 && known.Passages.Count >= 2
                && passages.All(p => p.Overlaps(known.Lines)) && known.Passages.All(p => p.Overlaps(lines)))
            {
                return known;
            }

            // Each qualifies what it asks about differently ("for contractors" / "for employees").
            if (DifferentConditions(context, candidate.Question, known.Question, mine, theirs, strongOnly: false) is not null)
            {
                continue;
            }

            // A question saved before v3 is located only by its links: one that points at another
            // section than the candidate's evidence asks about another passage, however alike the words.
            if (known.Legacy && places.Count > 0 && known.Places.Count > 0 && !SamePlace(places, known.Places))
            {
                continue;
            }

            var similarity = Jaccard(mine, theirs);
            if (similarity >= DuplicateSimilarity
                || (topic is not null && topic == known.TopicKey && similarity >= TopicDuplicateSimilarity)
                || (lines.Count > 0 && lines.SetEquals(known.Lines) && kind is not null && kind == known.Kind && similarity >= SameEvidenceSimilarity)
                || (known.Legacy && Covers(known.ContextTerms, terms) && !(places.Count > 0 && known.Places.Count > 0 && !SamePlace(places, known.Places)))
                || (known.AnsweredTerms is not null && similarity >= SameEvidenceSimilarity && Covers(known.AnsweredTerms, terms)))
            {
                return known;
            }
        }

        return null;
    }

    // Why two similarly worded questions still ask about different cases, else null - so neither is
    // dropped as a duplicate of the other (unknown is never taken as the same):
    //   numbers   both name numbers and they differ ("claims over £500" / "claims over £1,000");
    //   scope     the same workflow/scope dimension with different values ("new" / "amended referrals");
    //   setting   the same setting tested both ways ("when 'Auto-approve' is on" / "... is off");
    //   qualifier (strongOnly false) each has a term the other lacks that qualifies what it asks about -
    //             the first content word of a phrase opened by for/of/from/by/when/if/during/over/under/
    //             above/below/with/without/on/in/at, or a word written just before a word both share
    //             ("contractor expense claims" / "employee expense claims", "approves" / "reviews").
    //   one-sided (strongOnly false) only one question states a condition - a scope, a setting, or a
    //             qualifier that is a number, a named party (EntityNouns) or a term the source uses for a
    //             particular case (Context.Specific) - so it asks about a narrower case than the other.
    //             A one-sided qualifier that is padding ("in the system", "final") is not a difference.
    private static string? DifferentConditions(Context context, string a, string b, HashSet<string> aTerms, HashSet<string> bTerms, bool strongOnly)
    {
        var aNumbers = aTerms.Where(t => t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        var bNumbers = bTerms.Where(t => t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        if (aNumbers.Count > 0 && bNumbers.Count > 0 && !aNumbers.SetEquals(bNumbers))
        {
            return "numbers";
        }

        var aScopes = RuleRelations.ScopesOf(a);
        var bScopes = RuleRelations.ScopesOf(b);
        if (aScopes.Any(s => bScopes.TryGetValue(s.Key, out var v) && v != s.Value))
        {
            return "scope";
        }

        var bStates = RuleRelations.StatesOf(b);
        if (RuleRelations.StatesOf(a).Any(x => bStates.Any(y => RuleRelations.SameAttribute(x, y) && x.Positive is { } px && y.Positive is { } py && px != py)))
        {
            return "setting";
        }

        if (strongOnly)
        {
            return null;
        }

        var onlyA = aTerms.Where(t => !bTerms.Contains(t)).ToHashSet(StringComparer.Ordinal);
        var onlyB = bTerms.Where(t => !aTerms.Contains(t)).ToHashSet(StringComparer.Ordinal);
        var shared = aTerms.Where(bTerms.Contains).ToHashSet(StringComparer.Ordinal);
        var aQualifiers = QualifierTerms(context, a, shared).Where(onlyA.Contains).ToList();
        var bQualifiers = QualifierTerms(context, b, shared).Where(onlyB.Contains).ToList();
        if (aQualifiers.Count > 0 && bQualifiers.Count > 0)
        {
            return "qualifier";
        }

        // One question narrows the other ("Who approves expense claims?" / "... over £500?"): the narrow
        // case may well have its own answer, so the general question does not cover it - when what narrows
        // it is a real condition: a number, a workflow or other scope, a setting state, a named party, or a
        // term the source itself uses for a particular case. Padding the source never uses ("in the
        // system", "final approval") or its general vocabulary narrows nothing: the term overlap decides.
        if (aScopes.Keys.Any(k => !bScopes.ContainsKey(k)) || bScopes.Keys.Any(k => !aScopes.ContainsKey(k)))
        {
            return "one-sided scope";
        }

        var aStates = RuleRelations.StatesOf(a);
        if ((aStates.Count > 0) != (bStates.Count > 0))
        {
            return "one-sided setting";
        }

        // A source term narrows only when a qualifying phrase introduces it ("for employees"); a word just
        // before a shared one is often the sentence's own subject ("the portal respond").
        var phrased = QualifierTerms(context, a, shared, phrasesOnly: true).Where(onlyA.Contains)
            .Concat(QualifierTerms(context, b, shared, phrasesOnly: true).Where(onlyB.Contains)).ToHashSet(StringComparer.Ordinal);
        bool Narrows(string t) => t.Any(char.IsDigit) || EntityNouns.Contains(t) || (phrased.Contains(t) && context.Frequency(t) > 0 && context.Specific(t));
        return aQualifiers.Concat(bQualifiers).Any(Narrows) ? "one-sided condition" : null;
    }

    private static readonly HashSet<string> QualifyingPrepositions = new(StringComparer.Ordinal)
    {
        "for", "of", "from", "by", "when", "if", "during", "over", "under", "above", "below", "with", "without", "on", "in", "at"
    };

    private static HashSet<string> QualifierTerms(Context context, string question, HashSet<string> shared, bool phrasesOnly = false)
    {
        var qualifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phrase in Regex.Split(question.ToLowerInvariant(), @"[,;:.!?()]"))
        {
            var words = Word().Matches(phrase).Select(m => m.Value).ToList();
            var terms = words.Select(w => context.TermsOf(w).FirstOrDefault()).ToList();
            for (var i = 0; i < words.Count; i++)
            {
                if (QualifyingPrepositions.Contains(words[i]))
                {
                    if (Enumerable.Range(i + 1, Math.Min(3, words.Count - i - 1)).Select(k => terms[k]).FirstOrDefault(t => t is not null) is { } first)
                    {
                        qualifiers.Add(first);
                    }
                }
                else if (!phrasesOnly && terms[i] is { } term && i + 1 < words.Count && terms[i + 1] is { } next && shared.Contains(next) && !shared.Contains(term))
                {
                    qualifiers.Add(term);
                }
            }
        }

        return qualifiers;
    }

    // Words a "who" question uses only to ask for a person (stemmed as Terms stems them).
    // Lazy: the term tables it is stemmed with are initialised further down the file.
    private static readonly Lazy<HashSet<string>> RoleFillers = new(() => Terms(
        "responsible responsibility accountable role roles person people party parties owner owns actor authority"));

    private static HashSet<string> WithoutRoleFillers(HashSet<string> terms)
    {
        var core = terms.Where(t => !RoleFillers.Value.Contains(t) || t == "approv").ToHashSet(StringComparer.Ordinal);
        return core.Count > 0 ? core : terms;
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

    // "document" is a content word ("document type", "documents are sent via the API"), read the same as
    // "documents" and "documentation"; "apply" only links a question to its subject ("which rule applies").
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "if", "of", "to", "in", "on", "at", "by", "for", "with", "from", "as", "into",
        "about", "than", "then", "that", "this", "these", "those", "there", "their", "they", "them", "it", "its", "be", "been",
        "being", "is", "are", "was", "were", "am", "do", "does", "did", "done", "have", "has", "had", "having", "will", "would",
        "shall", "should", "must", "can", "could", "may", "might", "not", "no", "yes", "any", "all", "each", "every", "some",
        "such", "other", "own", "same", "so", "too", "very", "also", "only", "just", "what", "which", "who", "whom", "whose",
        "when", "where", "why", "how", "whether", "exactly", "specific", "specifically", "clarify", "clarification", "please",
        "define", "defined", "definition", "mean", "means", "meant", "system", "requirement", "requirements", "section",
        "srs", "specification", "stated", "state", "states", "say", "says", "said", "expected", "expect", "need",
        "apply", "applies", "applied", "applying",
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
        ["deacti"] = "disabl", ["inacti"] = "disabl",                                  // deactivate(d), inactive
        ["activa"] = "enabl", ["activ"] = "enabl",                                     // activate(d), active, activation
        ["compul"] = "mandat", ["obliga"] = "mandat",                                  // compulsory, obligatory
        ["sent"] = "send",                                                             // sent (irregular past of send)
    };

    // Expressions of several words read as the one word they mean before terms are taken: "signed off"
    // and "granted approval" are "approve", "turned off" is "disable". Only wording whose meaning does
    // not depend on the domain is listed; "create" / "generate" / "produce" are deliberately not merged.
    private static readonly (Regex Pattern, string Word)[] Phrases =
    [
        (new(@"\b(?:sign(?:s|ed|ing)?[- ]off(?: on)?|(?:grant(?:s|ed|ing)?|give(?:s|n)?|gave) (?:the |their |final )?approval(?: (?:for|of|to))?)\b", RegexOptions.Compiled), "approve"),
        (new(@"\b(?:turn(?:s|ed|ing)?|switch(?:es|ed|ing)?|toggle(?:s|d)?) off\b", RegexOptions.Compiled), "disable"),
        (new(@"\b(?:turn(?:s|ed|ing)?|switch(?:es|ed|ing)?|toggle(?:s|d)?) on\b", RegexOptions.Compiled), "enable"),
        (new(@"\bbrand[- ]new\b", RegexOptions.Compiled), "new"),
        (new(@"\bsubmit(?:s|ted|ting)? (?:it |them |the \w+ )?again\b|\bsent again\b", RegexOptions.Compiled), "resubmit")
    ];

    // Names of people or parties that the 6-letter stem would merge with a different word ("employer"
    // with "employee", "clinician" with "clinic", "referrer" with "referral", "applicant" with
    // "application"): each is kept as its own term so different entities are never read as one.
    private static readonly HashSet<string> EntityNouns = new(StringComparer.Ordinal)
    {
        "employer", "clinician", "contractor", "customer", "supplier", "provider", "consultant", "physician",
        "applicant", "participant", "administrator", "practitioner", "referrer", "requester", "subscriber", "manager"
    };

    // Content words of a text, lightly stemmed (one suffix stripped, then at most 6 letters) so
    // "approve", "approval" and "approved" are one term, and near-synonyms mapped to one (Synonyms:
    // "authorises" is "approve"; Phrases: "signed off" is "approve"). Entity names the stem would merge
    // with another word stay apart (EntityNouns). Numbers are kept as written. Terms a document itself
    // links ("Patient (also referred to as Client)") are merged per source by TermAliases, not here.
    public static HashSet<string> Terms(string? text) => Terms(text, semantic: true);

    // semantic: false reads words without Phrases and EntityNouns (synonyms only ever merge, so they stay)
    // - for the read-time relevance check, so no stored question loses an overlap it had under v3.
    internal static HashSet<string> Terms(string? text, bool semantic)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        var lower = (text ?? string.Empty).ToLowerInvariant();
        if (semantic)
        {
            foreach (var (pattern, replacement) in Phrases)
            {
                lower = pattern.Replace(lower, replacement);
            }
        }

        foreach (Match match in Word().Matches(lower))
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

            if (semantic && (EntityNouns.Contains(word) || (word.EndsWith('s') && EntityNouns.Contains(word[..^1]))))
            {
                terms.Add(word.EndsWith('s') && !EntityNouns.Contains(word) ? word[..^1] : word);
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

    // A sentence that itself leaves the point open answers nothing - including an open consideration
    // written as a note ("Consider how to send ARTP ...", "need consideration as to how ...", "Unsure how
    // this will be done", "@Riz to discuss with Kim ..."), a rule still to be written ("then specify rules
    // for different document types") or a suggestion ("suggest checkbox is added").
    [GeneratedRegex(@"\b(?i:tbd|tbc|to be (?:decided|determined|confirmed|defined|agreed|discussed|specified)|then specify|specify (?:the )?rules|rules? (?:needs?|have|has) to be (?:specified|defined|agreed)|suggest(?:s|ed|ion)?|as required|as appropriate|as needed|as necessary|where applicable|if applicable|etc|and so on|unclear|not yet (?:decided|defined|known|agreed)|unsure|undecided|to discuss|consider (?:how|whether|if|what|which|ability)|consideration (?:for|as to|of) (?:how|whether|what|which)|needs? (?:further )?consideration|consideration needs to be given|open (?:question|point|issue))\b")]
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

    // A question that asks how, how many, which, when or who for something it takes as given. Open
    // questions about behaviour ("What happens when ...", "How should ... be handled") presuppose nothing.
    [GeneratedRegex(@"^\s*(?i:how|what|which|when|who|whom|where)\b(?!(?i:\s+(?:happens|should happen|is the expected|does the system do)\b))(?!.*\b(?i:handled?|handling|treated)\b)")]
    private static partial Regex Presupposing();

    // Wording that shows the question does not take the point as settled.
    [GeneratedRegex(@"\b(?i:not|no|never|supports?|supported|whether|if|unless|despite|although|though|even|exceptions?|conflicts?|conflicting|contradict\w*|precedence|which rule|both|either|errors?|reject\w*|refus\w*|allowed|permitted|eligib\w*|exclud\w*|scope|valid\w*)\b")]
    private static partial Regex Acknowledges();

    // A passage that says something is not supported, not allowed or out of scope.
    [GeneratedRegex(@"\b(?i:not supported|unsupported|not (?:allowed|permitted|eligible|accepted|available|offered|in scope)|out of scope|(?:is|are) excluded|cannot be (?:sent|submitted|processed|used|requested|booked|referred)|(?:must|shall|will) not be (?:sent|submitted|processed|accepted|used|referred))\b")]
    private static partial Regex Denial();

    [GeneratedRegex(@"\b(?i:best practices?|industry standards?|any other|anything else|any (?:additional|further|more) (?:requirements?|features?|information|details?)|nice[- ]to[- ]have|future (?:enhancements?|releases?|versions?)|should we consider|do you have any|are there any (?:other|additional|further)|is there anything)\b")]
    private static partial Regex GenericQuestion();
}
