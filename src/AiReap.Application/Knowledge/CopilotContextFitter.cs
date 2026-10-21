using System.Text;
using AiReap.Application.Ai;
using AiReap.Application.Ai.Pipeline;

namespace AiReap.Application.Knowledge;

// One visible artifact the Copilot may show: Header is "CODE [Type, Status] Title"; Content is its
// flattened data (CopilotService.FlattenData, evidence quotes already removed).
public sealed record CopilotArtifactItem(string Code, string Header, string Content);

// One downstream artifact of an impact analysis, already rendered ("CODE (Type, Status): Title ...").
public sealed record CopilotImpactItem(string Line, bool ApprovedOrLater);

// The impact analysis of one artifact code named in the question. Items: approved-or-later first.
public sealed record CopilotImpactGroup(string Code, int ApprovedOrLaterCount, IReadOnlyList<CopilotImpactItem> Items);

// Everything a Copilot prompt can carry, all of it already limited to what the caller may see.
// Artifacts: most relevant first. Versions: newest first (at most StandardMaxVersions), out of
// TotalVersions. Excerpts: retrieval rank, and only for a caller who may read raw sources
// (ExcerptsAuthorized); ExcerptsPlaceholder is the block text when there are none to show.
public sealed record CopilotPromptInput(
    string Question,
    string Facts,
    IReadOnlyList<CopilotArtifactItem> Artifacts,
    IReadOnlyList<CopilotImpactGroup>? Impact,
    IReadOnlyList<string> Versions,
    int TotalVersions,
    bool ExcerptsAuthorized,
    IReadOnlyList<RetrievedChunk> Excerpts,
    string ExcerptsPlaceholder);

// A rendered Copilot prompt and the report of exactly what it carries. Fits: the prompt is within
// the budget it was fitted to (always true for the standard limits, which have no budget).
public sealed record CopilotFit(
    string UserPrompt, int EstimatedTokens, bool Fits, CopilotContextReport Report, IReadOnlyList<RetrievedChunk> SentExcerpts);

// Builds the Project Copilot's user prompt deterministically - identical input gives a byte-identical
// prompt and report - and decides what optional context it carries. Never an AI call.
//
// Never reduced: the system prompt, the STRUCTURED FACTS, every section header, the impact summary
// line of each artifact named in the question (its total and approved counts), the CONTEXT NOTE and
// the QUESTION, which is always the last block, verbatim.
//
// Standard limits (StandardFit, whichever provider serves the request): in relevance order, at most
// 40 artifacts carry content, 24,000 content characters in all; an artifact whose complete content
// no longer fits carries its first 600 characters instead ("content shortened") when that fits, else
// its title only; the artifact block stops at 28,000 characters (later artifacts are not shown). The
// 15 newest version-history edits are listed. CopilotService also retrieves at most 5 excerpts and
// analyses the impact of at most 3 codes named in the question.
//
// Provider budget (FitToBudget, after the serving provider refused the standard prompt): starting from
// the standard selection, these steps run in order, one item at a time, until the prompt fits the
// budget less the repair reserve - every step takes the least relevant item first:
//   1. title-only artifacts below the 5 most relevant are not shown;
//   2. version edits beyond the newest 5 are not listed;
//   3. artifacts below the top 5 are shortened to 600 characters;
//   4. excerpts beyond the top 2 are not shown (an excerpt is shown whole or not at all);
//   5. artifacts below the top 5 are shown by title only;
//   6. downstream impact items not yet approved are not listed (last listed first);
//   7. artifacts below the top 5 are not shown;
//   8. the remaining version edits, then 9. the remaining excerpts, are not shown;
//  10. the top 5 artifacts are shortened, then shown by title only, then not shown;
//  11. approved downstream impact items are not listed.
// A prompt within the budget less the reserve is used as soon as it is reached (the standard prompt
// itself when it already is). When no selection reaches that target, the smaller of the standard
// prompt and the fully reduced one is used if it fits the budget itself (its repair attempt may then
// be refused - safe, as a refused request is never sent); above the budget, Fits is false and the
// caller refuses the request. The report's Reason is "providerBudget" only when the budget steps
// changed the selection.
//
// Every reduction is counted in the report and stated in the prompt's CONTEXT NOTE, which tells the
// model that what is not shown still exists and must not be treated as absent.
public static class CopilotContextFitter
{
    public const int StandardMaxDetailedArtifacts = 40;
    public const int StandardMaxDetailedContentChars = 24_000;
    public const int TruncatedContentChars = 600;
    public const int StandardMaxArtifactBlockChars = 28_000;
    public const int StandardMaxVersions = 15;

    // Kept until the last budget steps: the most relevant artifacts, newest edits and best excerpts.
    public const int ProtectedArtifacts = 5;
    public const int ProtectedVersions = 5;
    public const int ProtectedExcerpts = 2;

    // Room kept free for GenerationSupport's one repair attempt (validation errors appended).
    public const int RepairReserveTokens = GenerationSupport.PartRepairReserveTokens;

    public const string NoRequirements = "(no requirements yet)";
    public const string NoEdits = "(no edits since generation)";
    public const string ShortenedMarker = " (content shortened): ";
    public const string TitleOnlyMarker = " (title only)";

    private enum Level { Full, Truncated, TitleOnly, Omitted }

    private sealed class Selection
    {
        public required Level[] Artifacts { get; init; }
        public required bool[] Versions { get; init; }
        public required bool[] Excerpts { get; init; }
        public required bool[][] Impact { get; init; }
    }

    public static CopilotFit StandardFit(string systemPrompt, CopilotPromptInput input)
    {
        var selection = Standard(input);
        var prompt = Render(input, selection, budgetProvider: null);
        return Result(input, selection, prompt, PromptBudgetReducer.EstimateRequestTokens(systemPrompt, prompt), fits: true, null, null);
    }

    public static CopilotFit FitToBudget(
        string systemPrompt, CopilotPromptInput input, string provider, int inputBudget, int reserveTokens = RepairReserveTokens)
    {
        var target = inputBudget - Math.Max(0, reserveTokens);
        var standard = StandardFit(systemPrompt, input);
        if (standard.EstimatedTokens <= target)
        {
            return standard;
        }

        var selection = Standard(input);
        int Estimate() => PromptBudgetReducer.EstimateRequestTokens(systemPrompt, Render(input, selection, provider));
        var changed = false;
        foreach (var change in Reductions(input, selection))
        {
            if (change())
            {
                changed = true;
                if (Estimate() <= target)
                {
                    break;
                }
            }
        }

        // The report names the provider budget only when it actually reduced something.
        var budgetProvider = changed ? provider : null;
        var prompt = Render(input, selection, budgetProvider);
        var tokens = PromptBudgetReducer.EstimateRequestTokens(systemPrompt, prompt);

        // The CONTEXT NOTE a reduction adds costs tokens too: when no reduction reaches the target and
        // the standard prompt itself fits the budget (without the repair reserve), it is the better one.
        if (tokens > target && standard.EstimatedTokens <= inputBudget && standard.EstimatedTokens <= tokens)
        {
            return standard;
        }

        return Result(input, selection, prompt, tokens, tokens <= inputBudget, budgetProvider, changed ? inputBudget : null);
    }

    private static Selection Standard(CopilotPromptInput input)
    {
        var levels = new Level[input.Artifacts.Count];
        int detailed = 0, detailedChars = 0, blockChars = 0;
        for (var i = 0; i < levels.Length; i++)
        {
            var content = input.Artifacts[i].Content;
            var level = Level.TitleOnly;
            if (detailed < StandardMaxDetailedArtifacts && detailedChars + content.Length <= StandardMaxDetailedContentChars)
            {
                level = Level.Full;
            }
            else if (detailed < StandardMaxDetailedArtifacts && content.Length > TruncatedContentChars
                     && detailedChars + TruncatedContentChars <= StandardMaxDetailedContentChars)
            {
                level = Level.Truncated;
            }

            if (blockChars + Line(input.Artifacts[i], level).Length + 1 > StandardMaxArtifactBlockChars)
            {
                level = blockChars + Line(input.Artifacts[i], Level.TitleOnly).Length + 1 <= StandardMaxArtifactBlockChars
                    ? Level.TitleOnly
                    : Level.Omitted;
            }

            levels[i] = level;
            if (level is Level.Full or Level.Truncated)
            {
                detailed++;
                detailedChars += level == Level.Full ? content.Length : TruncatedContentChars;
            }

            if (level != Level.Omitted)
            {
                blockChars += Line(input.Artifacts[i], level).Length + 1;
            }
        }

        return new Selection
        {
            Artifacts = levels,
            Versions = input.Versions.Select((_, i) => i < StandardMaxVersions).ToArray(),
            Excerpts = input.ExcerptsAuthorized ? input.Excerpts.Select(_ => true).ToArray() : [],
            Impact = (input.Impact ?? []).Select(g => g.Items.Select(_ => true).ToArray()).ToArray()
        };
    }

    // The budget steps, in order; each change reports whether it altered the selection.
    private static IEnumerable<Func<bool>> Reductions(CopilotPromptInput input, Selection s)
    {
        var artifactCount = s.Artifacts.Length;
        IEnumerable<int> LeastRelevantFirst(int from, int to) => Enumerable.Range(from, Math.Max(0, to - from)).Reverse();

        Func<bool> Artifact(int i, Level from, Level to) => () =>
        {
            if (s.Artifacts[i] != from || (to == Level.Truncated && input.Artifacts[i].Content.Length <= TruncatedContentChars))
            {
                return false;
            }

            s.Artifacts[i] = to;
            return true;
        };

        Func<bool> Demote(int i, Level to) => () =>
        {
            if (s.Artifacts[i] >= to)
            {
                return false;
            }

            s.Artifacts[i] = to;
            return true;
        };

        Func<bool> Drop(bool[] kept, int i) => () =>
        {
            if (!kept[i])
            {
                return false;
            }

            kept[i] = false;
            return true;
        };

        // 1. title-only artifacts below the top 5.
        foreach (var i in LeastRelevantFirst(ProtectedArtifacts, artifactCount)) yield return Artifact(i, Level.TitleOnly, Level.Omitted);
        // 2. version edits beyond the newest 5.
        foreach (var i in LeastRelevantFirst(ProtectedVersions, s.Versions.Length)) yield return Drop(s.Versions, i);
        // 3. shorten artifacts below the top 5.
        foreach (var i in LeastRelevantFirst(ProtectedArtifacts, artifactCount)) yield return Artifact(i, Level.Full, Level.Truncated);
        // 4. excerpts beyond the top 2.
        foreach (var i in LeastRelevantFirst(ProtectedExcerpts, s.Excerpts.Length)) yield return Drop(s.Excerpts, i);
        // 5. artifacts below the top 5 by title only.
        foreach (var i in LeastRelevantFirst(ProtectedArtifacts, artifactCount)) yield return Demote(i, Level.TitleOnly);
        // 6. impact items not yet approved, last listed first.
        foreach (var (g, i) in ImpactLastFirst(input, approved: false)) yield return Drop(s.Impact[g], i);
        // 7. artifacts below the top 5 not shown.
        foreach (var i in LeastRelevantFirst(ProtectedArtifacts, artifactCount)) yield return Demote(i, Level.Omitted);
        // 8. remaining version edits; 9. remaining excerpts.
        foreach (var i in LeastRelevantFirst(0, Math.Min(ProtectedVersions, s.Versions.Length))) yield return Drop(s.Versions, i);
        foreach (var i in LeastRelevantFirst(0, Math.Min(ProtectedExcerpts, s.Excerpts.Length))) yield return Drop(s.Excerpts, i);
        // 10. the top 5: shortened, then title only, then not shown.
        var top = Math.Min(ProtectedArtifacts, artifactCount);
        foreach (var i in LeastRelevantFirst(0, top)) yield return Artifact(i, Level.Full, Level.Truncated);
        foreach (var i in LeastRelevantFirst(0, top)) yield return Demote(i, Level.TitleOnly);
        foreach (var i in LeastRelevantFirst(0, top)) yield return Demote(i, Level.Omitted);
        // 11. approved impact items.
        foreach (var (g, i) in ImpactLastFirst(input, approved: true)) yield return Drop(s.Impact[g], i);
    }

    private static IEnumerable<(int Group, int Item)> ImpactLastFirst(CopilotPromptInput input, bool approved)
    {
        var groups = input.Impact ?? [];
        for (var g = groups.Count - 1; g >= 0; g--)
        {
            for (var i = groups[g].Items.Count - 1; i >= 0; i--)
            {
                if (groups[g].Items[i].ApprovedOrLater == approved)
                {
                    yield return (g, i);
                }
            }
        }
    }

    private static string Line(CopilotArtifactItem artifact, Level level) => level switch
    {
        Level.Full => artifact.Content.Length == 0 ? artifact.Header : $"{artifact.Header}: {artifact.Content}",
        Level.Truncated => $"{artifact.Header}{ShortenedMarker}{Cut(artifact.Content, TruncatedContentChars)}…",
        _ => artifact.Header + TitleOnlyMarker
    };

    // The first maxChars characters, never splitting a surrogate pair.
    private static string Cut(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var end = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..end];
    }

    private static string Render(CopilotPromptInput input, Selection s, string? budgetProvider)
    {
        var sb = new StringBuilder();
        sb.Append("STRUCTURED FACTS:\n").Append(input.Facts).Append("\n\n");

        sb.Append("PROJECT ARTIFACTS:\n");
        if (input.Artifacts.Count == 0)
        {
            sb.Append(NoRequirements);
        }
        else if (s.Artifacts.All(l => l == Level.Omitted))
        {
            sb.Append("(none shown - see CONTEXT NOTE)");
        }
        else
        {
            sb.AppendJoin('\n', input.Artifacts.Select((a, i) => (a, i)).Where(x => s.Artifacts[x.i] != Level.Omitted)
                .Select(x => Line(x.a, s.Artifacts[x.i])));
        }

        sb.Append("\n\n");

        if (input.Impact is { Count: > 0 } impact)
        {
            sb.Append("IMPACT ANALYSIS:\n");
            for (var g = 0; g < impact.Count; g++)
            {
                var group = impact[g];
                var total = group.Items.Count;
                if (total == 0)
                {
                    sb.Append($"{group.Code}: no downstream artifacts are linked to it.\n");
                    continue;
                }

                var kept = s.Impact[g].Count(k => k);
                sb.Append($"If {group.Code} changes, {total} downstream artifact(s) are potentially affected ({group.ApprovedOrLaterCount} already approved)");
                sb.Append(kept < total ? $"; {kept} listed below, {total - kept} not listed - see CONTEXT NOTE:\n" : ":\n");
                for (var i = 0; i < total; i++)
                {
                    if (s.Impact[g][i]) sb.Append("- ").Append(group.Items[i].Line).Append('\n');
                }
            }

            sb.Append('\n');
        }

        sb.Append("RECENT CHANGES:\n");
        if (input.TotalVersions == 0)
        {
            sb.Append(NoEdits);
        }
        else if (!s.Versions.Any(k => k))
        {
            sb.Append("(none shown - see CONTEXT NOTE)");
        }
        else
        {
            sb.AppendJoin('\n', input.Versions.Where((_, i) => s.Versions[i]));
        }

        sb.Append("\n\nDOCUMENT EXCERPTS:\n");
        var excerpts = SentExcerpts(input, s);
        if (!input.ExcerptsAuthorized || input.Excerpts.Count == 0)
        {
            sb.Append(input.ExcerptsPlaceholder);
        }
        else if (excerpts.Count == 0)
        {
            sb.Append("(matching excerpts not shown - see CONTEXT NOTE)");
        }
        else
        {
            sb.Append(KnowledgeRetriever.FormatExcerpts(excerpts));
        }

        sb.Append("\n\n");

        var report = Report(input, s, budgetProvider, null);
        if (report.Reduced)
        {
            sb.Append("CONTEXT NOTE:\n").Append(Note(report)).Append("\n\n");
        }

        sb.Append("QUESTION:\n").Append(input.Question);
        return sb.ToString();
    }

    private static string Note(CopilotContextReport r)
    {
        var why = r.Reason == CopilotContextReport.ProviderBudget
            ? "to fit the request budget of the AI provider serving this request"
            : "by the Copilot's standard context limits";
        var excerpts = r.ExcerptsOmitted is { } e ? $" Document excerpts not shown: {e}." : "";
        return $"Some project context above was shortened or not shown ({why}). Artifacts: {r.ArtifactsFull} with complete content, " +
               $"{r.ArtifactsTruncated} with content shortened, {r.ArtifactsTitleOnly} by title only, {r.ArtifactsOmitted} not shown. " +
               $"Version-history edits not shown: {r.VersionsOmitted}. Downstream impact items not listed: {r.ImpactCodesOmitted}.{excerpts} " +
               "Everything shortened or not shown still exists: never treat it as absent, and never conclude that something does not " +
               "exist, is missing or is untrue because it is not shown here. If the answer could depend on it, say the answer may be incomplete.";
    }

    private static List<RetrievedChunk> SentExcerpts(CopilotPromptInput input, Selection s) =>
        input.ExcerptsAuthorized ? input.Excerpts.Where((_, i) => s.Excerpts[i]).ToList() : [];

    private static CopilotContextReport Report(CopilotPromptInput input, Selection s, string? budgetProvider, int? inputBudget)
    {
        int Count(Level level) => s.Artifacts.Count(l => l == level);
        var versionsOmitted = input.TotalVersions - s.Versions.Count(k => k);
        var impactOmitted = s.Impact.Sum(g => g.Count(k => !k));
        int? excerptsOmitted = input.ExcerptsAuthorized ? s.Excerpts.Count(k => !k) : null;
        var reduced = Count(Level.Truncated) > 0 || Count(Level.TitleOnly) > 0 || Count(Level.Omitted) > 0
                      || versionsOmitted > 0 || impactOmitted > 0 || excerptsOmitted > 0;
        var reason = !reduced ? null : budgetProvider is null ? CopilotContextReport.StandardLimits : CopilotContextReport.ProviderBudget;
        var budgeted = reason == CopilotContextReport.ProviderBudget;
        return new CopilotContextReport(
            reduced, reason, budgeted ? budgetProvider : null, budgeted ? inputBudget : null,
            Count(Level.Full), Count(Level.Truncated), Count(Level.TitleOnly), Count(Level.Omitted),
            versionsOmitted, impactOmitted, excerptsOmitted);
    }

    private static CopilotFit Result(
        CopilotPromptInput input, Selection s, string prompt, int tokens, bool fits, string? provider, int? inputBudget) =>
        new(prompt, tokens, fits, Report(input, s, provider, inputBudget), SentExcerpts(input, s));
}
