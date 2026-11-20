using System.Text.Json;
using AiReap.Application.Artifacts;
using AiReap.Application.Artifacts.Payloads;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using AiReap.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

// Which clarification questions may be shown: only those whose source evidence is verified
// (ClarificationSourceReferences.IsGrounded). New questions pass this when they are made
// (ClarificationCandidateValidator); a question saved earlier is checked when it is read, against its
// source as it is now. Every place that lists, counts or opens questions for a person goes through here
// (the clarification endpoint, artifact lists and lookups, dashboard and lifecycle counts, the agents,
// the copilot, requirement generation's open-question context), so a hidden question cannot surface
// through another path. Nothing is written: a hidden question keeps its record, answer, status and
// history, and is shown again if its evidence becomes verifiable. No AI call - only the source text
// and the analysis responses already stored are read.
public static class ClarificationVisibility
{
    // For each question that may be shown, its data linked to its source (WithSourceLinks).
    public static async Task<Dictionary<Guid, string>> ShownAsync(
        IAiReapDbContext db, IReadOnlyCollection<Artifact> questions, CancellationToken cancellationToken)
    {
        var cqs = questions.Where(q => q.ArtifactType == ArtifactType.ClarificationQuestion && q.RequirementSourceId is not null).ToList();
        if (cqs.Count == 0)
        {
            return [];
        }

        var sourceIds = cqs.Select(q => q.RequirementSourceId!.Value).Distinct().ToList();
        var sources = await db.RequirementSources.AsNoTracking()
            .Where(s => sourceIds.Contains(s.Id))
            .Select(s => new { s.Id, s.RawText })
            .ToDictionaryAsync(s => s.Id, s => s.RawText, cancellationToken);
        var generations = sources.ToDictionary(s => s.Key, s => Results.SourceGeneration(s.Key, s.Value));

        // Linking a question re-reads its whole source (an index of a 250k-character SRS, every quote
        // located again), so a list of 20+ questions took seconds per read. The result depends only on
        // the question's data and version, the source text and the question's generation trace (written
        // with the question and never changed), so it is remembered per question and reused while all
        // of those are unchanged. Only the questions that changed, or were never seen, are linked again.
        var shown = new Dictionary<Guid, string>();
        var misses = new List<Artifact>();
        foreach (var question in cqs)
        {
            if (!generations.TryGetValue(question.RequirementSourceId!.Value, out var generation))
            {
                continue;
            }

            if (Results.TryGet(question, generation, out var cached))
            {
                if (cached is not null) shown[question.Id] = cached;
            }
            else
            {
                misses.Add(question);
            }
        }

        // A cold read (after a restart) links every question of a large source, and the dashboard and
        // stage pages ask for the same project's questions in parallel. One request per source does the
        // work while the others wait and then read its results, and the source's index is built once and
        // shared (it is read-only once built), instead of once per request.
        foreach (var group in misses.GroupBy(q => q.RequirementSourceId!.Value))
        {
            var generation = generations[group.Key];
            var gate = Results.GateFor(group.Key);
            await gate.WaitAsync(cancellationToken);
            try
            {
                var pending = new List<Artifact>();
                foreach (var question in group)
                {
                    if (Results.TryGet(question, generation, out var cached))
                    {
                        if (cached is not null) shown[question.Id] = cached;
                    }
                    else
                    {
                        pending.Add(question);
                    }
                }

                if (pending.Count == 0)
                {
                    continue;
                }

                var traces = await TracesAsync(db, pending.Where(q => ClarificationSourceReferences.NeedsEvidence(q.DataJson)).ToList(), cancellationToken);
                var locator = Results.LocatorFor(group.Key, generation, sources[group.Key]);
                foreach (var question in pending)
                {
                    var linked = ClarificationSourceReferences.WithSourceLinks(question.DataJson, group.Key, locator, traces.GetValueOrDefault(question.Id));
                    var grounded = ClarificationSourceReferences.IsGrounded(linked) ? linked : null;
                    Results.Set(question, generation, grounded);
                    if (grounded is not null)
                    {
                        shown[question.Id] = grounded;
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }

        return shown;
    }

    // Links and remembers every clarification question of the given sources (all sources when none are
    // given), so the first person to open a page after a restart does not wait for it. Read-only: the
    // database is only read. Used by the startup warm-up; returns how many questions were linked.
    // Limits: at most maxSources sources, most recently changed first (the ones people are working on),
    // and no new source is started once the time budget is spent.
    public static async Task<int> WarmAsync(
        IAiReapDbContext db, CancellationToken cancellationToken, int maxSources = int.MaxValue, TimeSpan? budget = null)
    {
        var sourceIds = (await db.Artifacts.AsNoTracking()
                .Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion && a.RequirementSourceId != null)
                .GroupBy(a => a.RequirementSourceId!.Value)
                .Select(g => new { SourceId = g.Key, Latest = g.Max(a => a.UpdatedAt) })
                .ToListAsync(cancellationToken))
            .OrderByDescending(s => s.Latest)
            .Take(Math.Max(0, maxSources))
            .Select(s => s.SourceId)
            .ToList();
        var deadline = budget is { } b ? DateTime.UtcNow + b : DateTime.MaxValue;

        var linked = 0;
        foreach (var sourceId in sourceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            var questions = await db.Artifacts.AsNoTracking()
                .Where(a => a.ArtifactType == ArtifactType.ClarificationQuestion && a.RequirementSourceId == sourceId)
                .ToListAsync(cancellationToken);
            await ShownAsync(db, questions, cancellationToken);
            linked += questions.Count;
        }

        return linked;
    }

    // In-process memory of linked questions (see ShownAsync). Nothing is persisted; a restart starts
    // empty. A source's text is compared exactly with the text last seen for it, and any change gives it
    // a new generation, so a question is never served a result linked to other text.
    public static class Results
    {
        private const int MaxQuestions = 20_000;
        private const int MaxSources = 2_000;

        private sealed record SourceEntry(string Text, long Generation);
        private sealed record QuestionEntry(string DataJson, int Version, Guid SourceId, long Generation, string? Linked);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SourceEntry> Sources = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, QuestionEntry> Questions = new();
        private static long _nextGeneration;

        public static long SourceGeneration(Guid sourceId, string? text)
        {
            text ??= string.Empty;
            if (Sources.TryGetValue(sourceId, out var known) && string.Equals(known.Text, text, StringComparison.Ordinal))
            {
                return known.Generation;
            }

            if (Sources.Count >= MaxSources) Sources.Clear();
            var entry = new SourceEntry(text, Interlocked.Increment(ref _nextGeneration));
            Sources[sourceId] = entry;
            return entry.Generation;
        }

        public static bool TryGet(Artifact question, long generation, out string? linked)
        {
            if (Questions.TryGetValue(question.Id, out var entry)
                && entry.Generation == generation
                && entry.SourceId == question.RequirementSourceId
                && entry.Version == question.CurrentVersion
                && string.Equals(entry.DataJson, question.DataJson, StringComparison.Ordinal))
            {
                linked = entry.Linked;
                return true;
            }

            linked = null;
            return false;
        }

        public static void Set(Artifact question, long generation, string? linked)
        {
            if (Questions.Count >= MaxQuestions) Questions.Clear();
            Questions[question.Id] = new QuestionEntry(question.DataJson, question.CurrentVersion, question.RequirementSourceId!.Value, generation, linked);
        }

        // One builder per source at a time (see ShownAsync).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

        public static SemaphoreSlim GateFor(Guid sourceId) => Gates.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));

        // The source's index, kept for the most recently used sources only (an index of a large SRS is
        // several MB) and only for the text it was built from.
        private const int MaxLocators = 32;
        private sealed record LocatorEntry(long Generation, ClarificationEvidenceLocator Locator);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, LocatorEntry> Locators = new();

        public static ClarificationEvidenceLocator LocatorFor(Guid sourceId, long generation, string? text)
        {
            if (Locators.TryGetValue(sourceId, out var entry) && entry.Generation == generation)
            {
                return entry.Locator;
            }

            if (Locators.Count >= MaxLocators) Locators.Clear();
            var locator = ClarificationEvidenceLocator.ForSource(sourceId, text);
            Locators[sourceId] = new LocatorEntry(generation, locator);
            return locator;
        }

        // For tests: forget everything, as a restart would.
        public static void Clear()
        {
            Questions.Clear();
            Sources.Clear();
            Locators.Clear();
        }
    }

    // The artifacts with every clarification question that may not be shown left out; other artifacts
    // are kept as they are.
    public static async Task<List<Artifact>> FilterAsync(
        IAiReapDbContext db, IReadOnlyCollection<Artifact> artifacts, CancellationToken cancellationToken)
    {
        var shown = await ShownAsync(db, artifacts, cancellationToken);
        return artifacts.Where(a => a.ArtifactType != ArtifactType.ClarificationQuestion || shown.ContainsKey(a.Id)).ToList();
    }

    // For each question: the RequirementAnalysis responses its audit rows kept (AIExecution, one row per
    // produced question) and its wording as first saved, when it has been edited since.
    private static async Task<Dictionary<Guid, ClarificationSourceReferences.GenerationTrace>> TracesAsync(
        IAiReapDbContext db, List<Artifact> questions, CancellationToken cancellationToken)
    {
        if (questions.Count == 0)
        {
            return [];
        }

        var ids = questions.Select(q => q.Id).ToList();
        var outputs = await db.AIExecutions.AsNoTracking()
            .Where(e => e.OperationType == "RequirementAnalysis" && e.ProducedArtifactId != null && ids.Contains(e.ProducedArtifactId.Value))
            .Select(e => new { ArtifactId = e.ProducedArtifactId!.Value, e.OutputJson })
            .ToListAsync(cancellationToken);
        var edited = questions.Where(q => q.CurrentVersion > 1).Select(q => q.Id).ToList();
        var firstVersions = edited.Count == 0
            ? []
            : await db.ArtifactVersions.AsNoTracking()
                .Where(v => v.VersionNumber == 1 && edited.Contains(v.ArtifactId))
                .Select(v => new { v.ArtifactId, v.DataSnapshotJson })
                .ToListAsync(cancellationToken);

        var outputsByQuestion = outputs.ToLookup(o => o.ArtifactId, o => o.OutputJson);
        var firstByQuestion = firstVersions.GroupBy(v => v.ArtifactId).ToDictionary(g => g.Key, g => g.First().DataSnapshotJson);
        return questions.ToDictionary(q => q.Id, q => new ClarificationSourceReferences.GenerationTrace(
            outputsByQuestion[q.Id].SelectMany(AnalysisResponseItems.Parse).ToList(),
            firstByQuestion.TryGetValue(q.Id, out var first) ? QuestionOf(first) : null));
    }

    private static string? QuestionOf(string dataJson)
    {
        try
        {
            return JsonSerializer.Deserialize<ClarificationQuestionPayload>(dataJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Question;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
