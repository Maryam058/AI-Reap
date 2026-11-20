using System.Text.RegularExpressions;

namespace AiReap.Application.Ai.Pipeline;

// One rule as read from a passage: the condition it applies under (empty when none is written) and what
// it says happens. NegativeOutcome: the outcome refuses, rejects or forbids something; Restrictive: the
// passage limits who or what may do something ("only", "must not", "at most"); Quantities: numbers with
// their unit ("24 hours"); ConditionStates / OutcomeStates: attributes the rule tests or sets
// ("Body Side = Bilateral", "'Is Careway' is disabled").
// Context: what the rule inherits from the document (RuleContextIndex), each item with its source line;
// InheritedStates: attributes the inherited context tests ("'Is Careway' is enabled" in a heading);
// Scope: the workflow/scope distinctions its own condition and inherited context state, one value per
// dimension ("workflow" = "create" | "update", ...) - a dimension stated both ways, or not at all, is
// left out (unknown); ExplicitException: the rule, or the lead-in/scope it sits under, says in words
// that it is an exception ("However", "except", "as an exception", "instead", "overrides").
public sealed record RuleStatement(
    int Line, string Text, string Condition, string Outcome, bool NegativeOutcome, bool NegativeCondition, bool Restrictive,
    IReadOnlyList<RuleQuantity> Quantities, IReadOnlyList<RuleState> ConditionStates, IReadOnlyList<RuleState> OutcomeStates,
    IReadOnlyList<InheritedContext> Context, IReadOnlyList<RuleState> InheritedStates,
    IReadOnlyDictionary<string, RuleScope> Scope, bool ExplicitException);

// One scope distinction and where it was read ("own condition", or the inherited context item).
public sealed record RuleScope(string Dimension, string Value, string Words, string From);

public sealed record RuleQuantity(string Unit, string Value);

// An attribute and its value as written; Positive is set for on/off states (enabled, disabled, true...).
public sealed record RuleState(string Attribute, IReadOnlySet<string> AttributeTerms, string Value, bool? Positive);

// How two passages a clarification question cites relate (RuleRelations.Compare).
//   SharedSubject       the words both passages are about (empty: they share no subject);
//   ExclusiveConditions why no single case can meet both rules' conditions, else null;
//   Conflict            why both outcomes cannot hold for the same case, else null (compatible rules);
//   Resolution          a passage elsewhere in the source that states how the two rules relate (an
//                       exception, a precedence, a scope), else null.
//   DifferentSubjects   the shared words name different things ("Referral notifications" / "Invoice
//                       notifications"), else null.
//   SameRule            both passages state the same rule in different words ("must be approved by the
//                       line manager" / "the line manager authorises every ..."), else null.
public sealed record RuleRelation(
    RuleStatement First, RuleStatement Second, IReadOnlyList<string> SharedSubject,
    string? ExclusiveConditions, string? Conflict, RuleResolution? Resolution, string? DifferentSubjects = null,
    string? SameRule = null);

public sealed record RuleResolution(int Line, string Text, string Marker);

// Deterministic comparison of two rule passages (no AI call, no I/O), used by
// ClarificationCandidateValidator before it accepts a question that sets one passage against another.
// It answers the questions a business analyst asks before raising a conflict:
//   1. do the two passages concern the same thing (a shared subject term)?
//   2. can one case meet both conditions, or do the conditions exclude each other (the same attribute
//      tested for different values, an on/off flag tested both ways, a field present in one and missing
//      in the other)?
//   3. do the outcomes actually clash (one refuses what the other does, one restricts, different numbers
//      for the same unit, different values set for the same attribute), or can both simply happen?
//   4. does another passage of the source say how the two relate ("does not apply to", "takes
//      precedence", "unless", "except") and name something from each rule?
// Context inheritance (RuleContextIndex): a rule is read with the headings that contain it, the lead-in
// of its list, the scope sentence of its section, its table's caption and header (a row's non-outcome
// cells are its conditions) and the definitions of terms it uses. Inherited context is used to tell
// scopes apart (workflow: new/create vs amend/update; initial submission vs resubmission; accepted vs
// rejected; individual vs bilateral; mandatory vs optional; on/off flags), never to invent a conflict:
// shared context is not a contradiction, outcomes are still compared, and a scope a rule does not state
// is unknown - it never makes two rules exclusive.
// Limits: all of it is lexical. A scope written in words outside the lists above is not recognised, a
// condition carried by prose that is not a lead-in, scope sentence or definition is not inherited, and
// a heading level is inferred from "#" depth or section numbering only. Every answer errs towards
// keeping the question: a pair is called compatible only when neither passage refuses or restricts
// anything and no number or value differs.
public static partial class RuleRelations
{
    // A passage's rule: the sentence(s) of its lines that the quote comes from, read with what it
    // inherits when the document structure is given. A table row is read with its column headers.
    public static RuleStatement Read(string text, int line = -1, string? quote = null, RuleContextIndex? document = null)
    {
        var context = document?.ContextOf(line) ?? [];
        string sentence, condition, outcome;
        if (document?.RowAt(line) is { } row)
        {
            (sentence, condition, outcome) = (row.Reading, row.Condition, row.Outcome);
        }
        else
        {
            sentence = FocusSentence(text, quote);
            (condition, outcome) = Split(sentence);
        }

        var scoping = context.Where(c => c.Kind is not "definition" and not "table-header").ToList();
        var inheritedStates = scoping.SelectMany(c => States(c.Kind == "heading" ? HeadingTitle(c.Text) : c.Text)).ToList();
        return new RuleStatement(
            line, sentence, condition, outcome,
            NegativeOutcome: Negative().IsMatch(outcome),
            NegativeCondition: condition.Length > 0 && (Negative().IsMatch(condition) || Absent().IsMatch(condition)),
            Restrictive: Restriction().IsMatch(sentence),
            Quantities: Quantities(sentence),
            ConditionStates: States(condition),
            OutcomeStates: States(outcome),
            Context: context,
            InheritedStates: inheritedStates,
            Scope: Scopes(condition, Subject(outcome), scoping),
            ExplicitException: ExceptionWording().IsMatch(sentence) || scoping.Any(c => c.Kind is "lead-in" or "scope" && ExceptionWording().IsMatch(c.Text)));
    }

    // aliases: terms the source itself says are the same thing (TermAliases), merged on both sides.
    public static RuleRelation Compare(
        RuleStatement first, RuleStatement second, IEnumerable<(int Line, string Text)> elsewhere, TermAliases? aliases = null)
    {
        aliases ??= TermAliases.None;
        var others = elsewhere.ToList();
        var firstTerms = aliases.Canon(ClarificationCandidateValidator.Terms(first.Text));
        var secondTerms = aliases.Canon(ClarificationCandidateValidator.Terms(second.Text));
        var shared = firstTerms.Where(t => secondTerms.Contains(t) && !t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        var conflict = Conflict(first, second, firstTerms, secondTerms);
        var exclusive = ExclusiveConditions(first, second, others, aliases);
        return new RuleRelation(
            first, second, Display(first.Text, shared, aliases),
            exclusive, conflict,
            Resolve(first, second, firstTerms, secondTerms, others, aliases),
            DifferentSubjects(first, second, shared, aliases),
            SameRule(first, second, firstTerms, secondTerms, conflict, exclusive));
    }

    // The scope distinctions a text states ("new", "amended", "resubmission" ...), one value per dimension
    // (a dimension written both ways is left out) - for comparing two questions' conditions.
    public static IReadOnlyDictionary<string, string> ScopesOf(string? text) =>
        Scopes(text ?? string.Empty, string.Empty, []).ToDictionary(s => s.Key, s => s.Value.Value, StringComparer.Ordinal);

    // The attributes a text tests or sets ("'Is Careway' is not enabled", "Body Side = Bilateral").
    public static IReadOnlyList<RuleState> StatesOf(string? text) => States(text ?? string.Empty);

    // ---- same rule ---------------------------------------------------------------------------------

    public const double SameRuleSimilarity = 0.6;

    // Two passages that say the same thing in other words: mostly the same terms (after synonyms, phrases
    // and the source's own aliases), the same polarity, and nothing that clashes or separates them.
    private static string? SameRule(RuleStatement a, RuleStatement b, HashSet<string> aTerms, HashSet<string> bTerms, string? conflict, string? exclusive)
    {
        if (conflict is not null || exclusive is not null || a.NegativeOutcome != b.NegativeOutcome)
        {
            return null;
        }

        var aWords = aTerms.Where(t => !t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        var bWords = bTerms.Where(t => !t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        return Jaccard(aWords, bWords) >= SameRuleSimilarity
            ? $"both say: {string.Join(", ", Display(a.Text, aWords.Where(bWords.Contains).ToHashSet(StringComparer.Ordinal), TermAliases.None).Take(6))}"
            : null;
    }

    // ---- 2. conditions -----------------------------------------------------------------------------

    private static string? ExclusiveConditions(RuleStatement a, RuleStatement b, List<(int Line, string Text)> elsewhere, TermAliases aliases)
    {
        // States the rule tests itself and those it inherits ("'Is Careway' is enabled" in its heading).
        foreach (var x in a.ConditionStates.Concat(a.InheritedStates))
        {
            foreach (var y in b.ConditionStates.Concat(b.InheritedStates).Where(y => SameAttribute(x, y)))
            {
                if (x.Positive is { } px && y.Positive is { } py && px != py)
                {
                    return $"one rule applies when {x.Attribute} is {x.Value}, the other when it is {y.Value}{From(a, x)}{From(b, y)}";
                }

                if (x.Positive is null && y.Positive is null && !string.Equals(Norm(x.Value), Norm(y.Value), StringComparison.Ordinal))
                {
                    return $"one rule applies when {x.Attribute} = {x.Value}, the other when {y.Attribute} = {y.Value}{From(a, x)}{From(b, y)}";
                }
            }
        }

        // Different workflows or scopes, each stated (by the rule or the context it inherits) - a scope
        // only one rule states is unknown for the other, never assumed to differ.
        foreach (var (dimension, x) in a.Scope)
        {
            if (b.Scope.TryGetValue(dimension, out var y) && x.Value != y.Value)
            {
                return $"they belong to different {DimensionName(dimension)}: \"{x.Words}\" ({x.From}) against \"{y.Words}\" ({y.From})";
            }
        }

        // A general rule and a rule for a narrower case: the narrower one is an exception to the general
        // one only when it says so in words, or when its condition is an explicit on/off setting that is
        // established to govern what the general rule is about ("If 'Is Careway' is disabled, referrals are
        // not sent" against a rule about sending to Careway). Any other condition is not assumed to settle
        // the general rule: "Wrist/hand is not supported" against "If Body Side = Bilateral ... Wrist, call
        // the API twice" stays a question, and so does a setting nothing links to the general rule.
        if (a.ExplicitException != b.ExplicitException)
        {
            var exception = a.ExplicitException ? a : b;
            return $"one rule is stated as an explicit exception to the other (\"{Shorten(exception.Text)}\")";
        }

        if ((a.Condition.Length == 0) != (b.Condition.Length == 0))
        {
            var (general, specific) = a.Condition.Length == 0 ? (a, b) : (b, a);
            var generalTerms = aliases.Canon(ClarificationCandidateValidator.Terms(general.Text));
            if (ConditionTerms(specific.Condition).Any(t => !generalTerms.Contains(t)))
            {
                foreach (var setting in specific.ConditionStates.Where(s => s.Positive is not null))
                {
                    if (Governs(setting, general, specific, generalTerms, elsewhere, aliases) is { } how)
                    {
                        return $"one is the general rule and the other applies only when \"{Shorten(specific.Condition.Trim())}\", an explicit setting that makes it an exception ({how})";
                    }
                }
            }
        }

        // A field present in one condition and missing in the other ("contains a date of birth" /
        // "is missing the date of birth"): mostly the same condition words, opposite sense.
        if (a.Condition.Length > 0 && b.Condition.Length > 0 && a.NegativeCondition != b.NegativeCondition)
        {
            var ca = ConditionTerms(a.Condition);
            var cb = ConditionTerms(b.Condition);
            var common = ca.Count(cb.Contains);
            if (common >= 2 && common * 2 >= Math.Min(ca.Count, cb.Count))
            {
                var negative = a.NegativeCondition ? a : b;
                return $"one rule applies only when \"{Shorten(negative.Condition.Trim())}\", the other in the opposite case";
            }
        }

        return null;
    }

    // Where a state was read: nothing for the rule's own condition, else the inherited item it came from.
    private static string From(RuleStatement rule, RuleState state)
    {
        if (rule.ConditionStates.Contains(state))
        {
            return string.Empty;
        }

        var item = rule.Context.FirstOrDefault(c => c.Text.Contains(state.Attribute, StringComparison.OrdinalIgnoreCase));
        return item is null ? string.Empty : $"; \"{Shorten(item.Text)}\" is inherited from the {item.Kind} at line {item.Line + 1}";
    }

    // How a setting is known to govern what a rule is about, else null (not established, so it settles
    // nothing). Similar wording alone never links them ("'Approval emails'" shares "approval" with a rule
    // about leave approval, and says nothing about whether it decides that approval). Established when:
    //   another passage says the setting controls what the rule concerns ("The 'Fast track' setting
    //   controls whether line manager approval is needed."); or
    //   the setting's own rule reverses the general rule's own action - opposite polarity, and the first
    //   clause of its outcome uses nothing but the general rule's words (at least two besides the name) -
    //   and the setting is named wholly after what the general rule is about ("If 'Is Careway' is
    //   disabled, referrals are not sent to Careway" against "Axis sends referrals to Careway"). Both are
    //   required, never the name alone. A rule that states a different action under the setting ("If 'Is
    //   Careway' is enabled, Wrist referrals are sent to the API twice" against "Wrist/hand is not
    //   supported by Careway") reverses nothing: it clashes, and the question stays.
    private static string? Governs(
        RuleState setting, RuleStatement general, RuleStatement specific, HashSet<string> ruleTerms,
        List<(int Line, string Text)> elsewhere, TermAliases aliases)
    {
        var name = aliases.Canon(setting.AttributeTerms.ToHashSet(StringComparer.Ordinal));
        var action = aliases.Canon(ClarificationCandidateValidator.Terms(Regex.Split(specific.Outcome, @"[,;]|\b(?:and|but|then)\b")[0]));
        action.ExceptWith(name);
        if (name.Count > 0 && name.IsSubsetOf(ruleTerms) && general.NegativeOutcome != specific.NegativeOutcome
            && action.Count >= 2 && action.IsSubsetOf(ruleTerms))
        {
            return $"the setting \"{setting.Attribute.Trim('\'', '"', ' ')}\" is named after what the general rule is about and its own rule switches that behaviour the other way";
        }

        // What it controls - the words after "controls" - must name the rule's own subject (two of its
        // terms besides the setting's name): "controls reminder emails sent to managers" is about reminder
        // emails, not about the approval a rule on managers' approval concerns.
        foreach (var (line, text) in elsewhere)
        {
            var terms = aliases.Canon(ClarificationCandidateValidator.Terms(text));
            if (name.Count > 0 && name.IsSubsetOf(terms) && Control().Match(text) is { Success: true } control
                && aliases.Canon(ClarificationCandidateValidator.Terms(text[(control.Index + control.Length)..])).Count(t => ruleTerms.Contains(t) && !name.Contains(t)) >= 2)
            {
                return $"line {line + 1} says the setting controls it: \"{Shorten(text)}\"";
            }
        }

        return null;
    }

    private static string DimensionName(string dimension) => dimension switch
    {
        "workflow" => "workflows",
        "submission" => "submissions (initial or resubmission)",
        "decision" => "decision outcomes",
        "laterality" => "cases (individual or bilateral)",
        "obligation" => "obligations (mandatory or optional)",
        _ => "scopes"
    };

    // The scope distinctions a rule states: in its own condition first, then its subject ("Modified
    // referrals" in "Modified referrals do not need a GP letter"), then each inherited item (a heading's
    // title, lead-in, scope sentence, table caption). A dimension stated with two values is ambiguous and
    // dropped.
    private static Dictionary<string, RuleScope> Scopes(string condition, string subject, IReadOnlyList<InheritedContext> context)
    {
        var found = new List<RuleScope>();
        void Read(string text, string from)
        {
            foreach (var (dimension, value, pattern) in ScopeWords)
            {
                if (pattern.Match(text) is { Success: true } m)
                {
                    found.Add(new RuleScope(dimension, value, m.Value, from));
                }
            }
        }

        Read(condition, "its own condition");
        Read(subject, "its subject");
        foreach (var c in context)
        {
            Read(c.Kind == "heading" ? HeadingTitle(c.Text) : c.Text, $"{c.Kind} \"{Shorten(c.Text)}\", line {c.Line + 1}");
        }

        return found.GroupBy(s => s.Dimension)
            .Where(g => g.Select(s => s.Value).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private static readonly (string Dimension, string Value, Regex Pattern)[] ScopeWords =
    [
        ("workflow", "create", new(@"\b(?:new|newly|creat(?:e|es|ed|ing|ion))\b", RegexOptions.IgnoreCase)),
        ("workflow", "update", new(@"\b(?:amend(?:s|ed|ing|ments?)?|updat(?:e|es|ed|ing)|modif(?:y|ies|ied|ications?)|chang(?:e|es|ed)|edit(?:s|ed)?|revis(?:e|es|ed|ing|ions?)|alter(?:s|ed|ations?)?|existing)\b", RegexOptions.IgnoreCase)),
        ("submission", "initial", new(@"\b(?:initial|first|original) (?:submissions?|requests?|referrals?|applications?)\b|\bfirst[- ]time\b|\bsubmitted for the first time\b", RegexOptions.IgnoreCase)),
        ("submission", "resubmission", new(@"\bre-?submi(?:t|ts|tted|tting|ssions?)\b|\bsubmit(?:s|ted|ting)? (?:\w+ )?again\b|\bsent again\b|\bsecond submission\b", RegexOptions.IgnoreCase)),
        ("decision", "accepted", new(@"\b(?:accepted|approved|authori[sz]ed|granted)\b", RegexOptions.IgnoreCase)),
        ("decision", "rejected", new(@"\b(?:rejected|declined|denied|refused|turned down)\b", RegexOptions.IgnoreCase)),
        ("laterality", "individual", new(@"\b(?:individual|unilateral|single[- ]sided)\b", RegexOptions.IgnoreCase)),
        ("laterality", "bilateral", new(@"\bbilateral\b", RegexOptions.IgnoreCase)),
        ("obligation", "mandatory", new(@"(?<!\bnot )\b(?:mandatory|compulsory|obligatory|required)\b", RegexOptions.IgnoreCase)),
        ("obligation", "optional", new(@"\b(?:optional|not (?:mandatory|compulsory|obligatory|required))\b", RegexOptions.IgnoreCase))
    ];

    // "## 4.2 Referral amendments" -> "Referral amendments".
    private static string HeadingTitle(string heading) =>
        Regex.Replace(heading.Trim(), @"^#{1,6}\s*|^(?:\d+(?:\.\d+)*\.?|(?i:section|chapter|part|appendix)\s+\S+)\s*", string.Empty);

    // ---- different subjects ------------------------------------------------------------------------

    // A shared word each rule qualifies differently - in its own text or its heading/caption - with a
    // qualifier the other rule never mentions: "Referral notifications" and "Invoice notifications",
    // "approved leave requests" and "draft leave requests" are different things.
    private static string? DifferentSubjects(RuleStatement a, RuleStatement b, HashSet<string> shared, TermAliases aliases)
    {
        var aWords = Surroundings(a);
        var bWords = Surroundings(b);
        var aAll = aliases.Canon(ClarificationCandidateValidator.Terms(string.Join(" ", aWords)));
        var bAll = aliases.Canon(ClarificationCandidateValidator.Terms(string.Join(" ", bWords)));
        foreach (var term in shared)
        {
            var aQualifiers = Qualifiers(aWords, term, aliases).Where(q => !bAll.Contains(q.Term)).ToList();
            var bQualifiers = Qualifiers(bWords, term, aliases).Where(q => !aAll.Contains(q.Term)).ToList();
            if (aQualifiers.Count > 0 && bQualifiers.Count > 0)
            {
                return $"\"{aQualifiers[0].Phrase}\" against \"{bQualifiers[0].Phrase}\"";
            }
        }

        return null;
    }

    // The rule's text and the titles of its headings and table caption.
    private static List<string> Surroundings(RuleStatement rule) =>
        [rule.Text, .. rule.Context.Where(c => c.Kind is "heading" or "table-title").Select(c => c.Kind == "heading" ? HeadingTitle(c.Text) : c.Text)];

    // The content word written just before the term ("Referral notifications" -> referral), per text -
    // within one phrase: "for the client, referrals" does not make "client" a qualifier of "referrals".
    private static IEnumerable<(string Term, string Phrase)> Qualifiers(List<string> texts, string term, TermAliases aliases)
    {
        foreach (var text in texts.SelectMany(t => Regex.Split(t, @"[,;:.!?()\[\]]|\s[-–]\s")))
        {
            var words = Word().Matches(text).Select(m => m.Value).ToList();
            for (var i = 1; i < words.Count; i++)
            {
                if (Term(words[i], aliases) != term)
                {
                    continue;
                }

                var before = Term(words[i - 1], aliases);
                if (before is not null && before != term && !before.Any(char.IsDigit))
                {
                    yield return (before, $"{words[i - 1]} {words[i]}");
                }
            }
        }
    }

    private static string? Term(string word, TermAliases aliases) =>
        ClarificationCandidateValidator.Terms(word).FirstOrDefault() is { } t ? aliases.Canon(t) : null;

    public static bool SameAttribute(RuleState x, RuleState y) =>
        x.AttributeTerms.Count > 0 && y.AttributeTerms.Count > 0
        && (x.AttributeTerms.IsSubsetOf(y.AttributeTerms) || y.AttributeTerms.IsSubsetOf(x.AttributeTerms));

    private static HashSet<string> ConditionTerms(string condition)
    {
        var terms = ClarificationCandidateValidator.Terms(condition);
        terms.ExceptWith(ClarificationCandidateValidator.Terms("missing absent without blank empty null provided present contains includes supplied given"));
        return terms;
    }

    // ---- 3. outcomes -------------------------------------------------------------------------------

    private static string? Conflict(RuleStatement a, RuleStatement b, HashSet<string> aTerms, HashSet<string> bTerms)
    {
        if (a.NegativeOutcome != b.NegativeOutcome)
        {
            var (no, yes) = a.NegativeOutcome ? (a, b) : (b, a);
            return $"one rule refuses (\"{Shorten(no.Outcome.Trim())}\") where the other acts (\"{Shorten(yes.Outcome.Trim())}\")";
        }

        foreach (var x in a.Quantities)
        {
            if (b.Quantities.FirstOrDefault(y => y.Unit == x.Unit && y.Value != x.Value) is { } y)
            {
                return $"the rules give different values for the same unit ({x.Value} against {y.Value})";
            }
        }

        foreach (var x in a.OutcomeStates)
        {
            if (b.OutcomeStates.FirstOrDefault(y => SameAttribute(x, y) && !string.Equals(Norm(x.Value), Norm(y.Value), StringComparison.Ordinal)) is { } y)
            {
                return $"the rules set {x.Attribute} to different values ({x.Value} against {y.Value})";
            }
        }

        // Opposite obligation or decision words in the outcomes ("is mandatory" / "is optional").
        foreach (var (dimension, value, pattern) in ScopeWords.Where(w => w.Dimension is "obligation" or "decision"))
        {
            if (pattern.Match(a.Outcome) is { Success: true } x
                && ScopeWords.Any(o => o.Dimension == dimension && o.Value != value && o.Pattern.IsMatch(b.Outcome))
                && !ScopeWords.Any(o => o.Dimension == dimension && o.Value != value && o.Pattern.IsMatch(a.Outcome)))
            {
                return $"one rule makes it {x.Value.ToLowerInvariant()}, the other the opposite";
            }
        }

        // One restriction against a rule that allows: a clash. Two restrictions clash only when they
        // restrict differently - "Only managers may approve leave" and "Leave may be approved by managers
        // only" are the same restriction in other words.
        if (a.Restrictive != b.Restrictive)
        {
            var restrictive = a.Restrictive ? a : b;
            return $"one rule restricts what the other allows (\"{Shorten(restrictive.Text)}\")";
        }

        if (a.Restrictive && b.Restrictive && Jaccard(aTerms, bTerms) < SameRuleSimilarity)
        {
            return $"the rules restrict it differently (\"{Shorten(a.Text)}\" against \"{Shorten(b.Text)}\")";
        }

        return null;
    }

    // ---- 4. resolution elsewhere ---------------------------------------------------------------------

    private static RuleResolution? Resolve(
        RuleStatement a, RuleStatement b, HashSet<string> aTerms, HashSet<string> bTerms, IEnumerable<(int Line, string Text)> elsewhere, TermAliases aliases)
    {
        var sentences = elsewhere.Select(s => (s.Line, s.Text, Terms: aliases.Canon(ClarificationCandidateValidator.Terms(s.Text)))).ToList();

        // Words a long document uses throughout ("Careway", "referral") name its general subject, not
        // either rule: a sentence that shares only those with a rule is not about it.
        var common = EvidenceSupport.CommonTerms(sentences.Select(s => s.Terms).ToList());
        var onlyA = aTerms.Where(t => !bTerms.Contains(t) && !common.Contains(t)).ToHashSet(StringComparer.Ordinal);
        var onlyB = bTerms.Where(t => !aTerms.Contains(t) && !common.Contains(t)).ToHashSet(StringComparer.Ordinal);
        foreach (var (line, text, terms) in sentences)
        {
            if (text.EndsWith('?') || Resolver().Match(text) is not { Success: true } marker)
            {
                continue;
            }

            // It names something only the first rule has and something only the second has, so it is
            // about these two rules - and it is not just one of them written again.
            if (terms.Overlaps(onlyA) && terms.Overlaps(onlyB) && Jaccard(terms, aTerms) < 0.8 && Jaccard(terms, bTerms) < 0.8)
            {
                return new RuleResolution(line, text, marker.Value);
            }
        }

        return null;
    }

    // ---- reading -------------------------------------------------------------------------------------

    // The sentence(s) of the passage that hold the quote; the whole passage when it cannot tell.
    private static string FocusSentence(string text, string? quote)
    {
        var sentences = SentenceBreak().Split(text.Trim()).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (sentences.Count <= 1 || string.IsNullOrWhiteSpace(quote))
        {
            return text.Trim();
        }

        var normalizedQuote = SourceEvidence.Normalize(quote);
        var holding = sentences.Where(s => SourceEvidence.Normalize(s).Contains(normalizedQuote, StringComparison.Ordinal)).ToList();
        if (holding.Count > 0)
        {
            return holding[0];
        }

        var quoteTerms = ClarificationCandidateValidator.Terms(quote);
        var parts = sentences.Where(s => ClarificationCandidateValidator.Terms(s).Count(quoteTerms.Contains) >= Math.Min(3, quoteTerms.Count)).ToList();
        return parts.Count > 0 ? string.Join(" ", parts) : text.Trim();
    }

    // "If <condition>, <outcome>" (the comma that starts the main clause, not one inside a list), or
    // "<outcome> if|when|unless <condition>". No condition: the whole sentence is the outcome.
    private static (string Condition, string Outcome) Split(string sentence)
    {
        var leading = LeadingCondition().Match(sentence);
        if (leading.Success)
        {
            var body = leading.Groups["rest"].Value;
            var commas = Enumerable.Range(0, body.Length).Where(i => body[i] == ',').ToList();
            foreach (var comma in commas)
            {
                var next = body.IndexOf(',', comma + 1);
                var segment = body[(comma + 1)..(next < 0 ? body.Length : next)];
                if (MainClause().IsMatch(segment))
                {
                    return (body[..comma], body[(comma + 1)..]);
                }
            }

            var then = Regex.Match(body, @"\bthen\b", RegexOptions.IgnoreCase);
            if (then.Success)
            {
                return (body[..then.Index], body[(then.Index + then.Length)..]);
            }

            if (commas.Count > 0)
            {
                return (body[..commas[^1]], body[(commas[^1] + 1)..]);
            }
        }

        var trailing = TrailingCondition().Match(sentence);
        return trailing.Success ? (trailing.Groups["c"].Value, trailing.Groups["o"].Value) : (string.Empty, sentence);
    }

    // The words before the main verb of an outcome ("Modified referrals" in "Modified referrals do not
    // need a GP letter"); empty when no modal or auxiliary is found within its first words.
    private static string Subject(string outcome)
    {
        var m = SubjectEnd().Match(outcome);
        return m.Success && m.Index <= 80 ? outcome[..m.Index] : string.Empty;
    }

    private static List<RuleQuantity> Quantities(string text) => Quantity().Matches(text)
        .Select(m => (Value: m.Groups["n"].Value, Unit: ClarificationCandidateValidator.Terms(m.Groups["u"].Value).FirstOrDefault()))
        .Where(q => q.Unit is not null)
        .Select(q => new RuleQuantity(q.Unit!, q.Value))
        .Distinct()
        .ToList();

    private static List<RuleState> States(string text)
    {
        var states = new List<RuleState>();
        if (text.Length == 0)
        {
            return states;
        }

        foreach (Match m in Flag().Matches(text))
        {
            var attribute = m.Groups["a"].Value.Trim();
            var value = m.Groups["v"].Value.ToLowerInvariant();
            var positive = value is "enabled" or "on" or "true" or "active" or "activated" or "yes" or "ticked" or "checked" or "selected" or "set";
            var negated = m.Groups["not"].Success;
            states.Add(new RuleState(attribute, AttributeTerms(attribute), negated ? $"not {value}" : value, negated ? !positive : positive));
        }

        foreach (Match m in Assignment().Matches(text))
        {
            var attribute = m.Groups["a"].Value.Trim();
            states.Add(new RuleState(attribute, AttributeTerms(attribute), m.Groups["v"].Value, null));
        }

        return states;
    }

    // The attribute's own words: the last three before the operator ("If Body Side" -> body, side).
    private static HashSet<string> AttributeTerms(string attribute)
    {
        var words = attribute.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return ClarificationCandidateValidator.Terms(string.Join(' ', words.Skip(Math.Max(0, words.Length - 3))));
    }

    // The words of the text whose terms are in the set, as written (first spelling of each term).
    private static List<string> Display(string text, HashSet<string> terms, TermAliases aliases)
    {
        var shown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match word in Word().Matches(text))
        {
            var term = Term(word.Value, aliases);
            if (term is not null && terms.Contains(term) && seen.Add(term))
            {
                shown.Add(word.Value);
            }
        }

        return shown;
    }

    private static string Norm(string value) => string.Join(' ', ClarificationCandidateValidator.Terms(value).Order());

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count(b.Contains);
        return shared / (double)(a.Count + b.Count - shared);
    }

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..117] + "...";

    [GeneratedRegex(@"(?<=[.;!?])\s+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    // "For bilateral Wrist referrals, call the API twice." counts; "For example, ..." does not.
    [GeneratedRegex(@"^\s*(?:if|when|whenever|where|unless|once|in case|in the event that|for(?! example| instance))\b(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LeadingCondition();

    [GeneratedRegex(@"^(?<o>.+?)\b(?:if|when|whenever|unless|provided that|only if|in case)\b(?<c>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TrailingCondition();

    // A segment after a comma that starts the main clause: a modal, or a clause-opening word or imperative
    // ("is"/"are" alone do not count: "If X, Body Part is Knee, ..." is still the condition).
    [GeneratedRegex(@"\b(?:will|shall|must|should|may|can|cannot|then)\b|^\s*(?:the|an?|this|that|these|those|it|they|call|send|return|reject|display|show|create|set|raise|log|notify|route|use|apply|skip|ignore|record|store|generate|block|allow|deny|refuse|redirect|forward|invoke|trigger)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MainClause();

    [GeneratedRegex(@"\b(?:not|no|never|cannot|can't|won't|unsupported|prohibited|forbidden|disallowed|denied|rejects?|rejected|errors?|fails?|failed|failure|blocks?|blocked|disabled|excluded|invalid|refuses?|refused|declined?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Negative();

    [GeneratedRegex(@"\b(?:missing|absent|without|blank|empty|null)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Absent();

    [GeneratedRegex(@"\b(?:only|solely|exclusively|must not|shall not|may not|cannot|can't|never|not (?:be )?(?:allowed|permitted)|no more than|no fewer than|at most|at least|maximum|minimum|limited to)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Restriction();

    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*-?\s*(?:working |business |calendar )?(?<u>[\p{L}]{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex Quantity();

    // "'Is Careway' is disabled", "Auto-approve is turned off", "the setting is not enabled" (negated).
    [GeneratedRegex(@"['""‘’“”]?(?<a>[\p{L}][\p{L}\p{N} _/-]{0,40}?)['""‘’“”]?\s+(?:is|are|has been|have been|was|were)\s+(?<not>not\s+)?(?:set to\s+|turned\s+|switched\s+)?(?<v>enabled|disabled|on|off|true|false|active|inactive|activated|deactivated|yes|no|ticked|unticked|checked|unchecked|selected|deselected|set|unset)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Flag();

    // Where the subject of an outcome ends: its modal or auxiliary verb.
    [GeneratedRegex(@"\b(?:must|shall|should|will|may|can|cannot|need(?:s)? to|do not|does not|don't|doesn't|is|are|require[sd]?|need(?:s)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectEnd();

    // A sentence saying a setting controls something.
    [GeneratedRegex(@"\b(?:controls?|determines?|governs?|decides?|switch(?:es)? (?:on|off)|turns? (?:on|off)|enables?|disables?|bypass(?:es)?|skips?|overrides?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Control();

    [GeneratedRegex(@"(?<a>[\p{L}][\p{L}\p{N} _/-]{0,40}?)\s*(?:==|=|\bequals\b|\bis set to\b)\s*['""‘’“”]?(?<v>[\p{L}\p{N}_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();

    // A rule that calls itself an exception.
    [GeneratedRegex(@"\b(?:however|except|exceptions?|as an exception|notwithstanding|instead|overrides?|takes precedence)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionWording();

    // Wording that states how rules relate: an exception, a precedence or a scope. "Exception" on its own
    // is not enough - documents also use it for an error case ("raise an exception", "become an
    // exception for manual verification", "exception menu"); only wording that makes one rule an
    // exception to another counts.
    [GeneratedRegex(@"\b(?:except(?:\s+(?:for|when|where|if|that))?(?!ions?)|with the exception of|as an exception|(?:is|are) an? exceptions? (?:to|for)|exceptions? to (?:the|this|these|that|any) |the following exceptions|exceptions? (?:apply|applies)|unless|excluding|other than|takes? precedence|precedence over|overrides?|overridden|appl(?:y|ies) only|only appl(?:y|ies)|(?:does|do|shall|will) not apply|is not applied|are not applied|never appl(?:y|ies)|regardless of|instead of|prior to|(?:is|are) (?:checked|validated|evaluated|applied) (?:first|before))\b", RegexOptions.IgnoreCase)]
    private static partial Regex Resolver();
}
