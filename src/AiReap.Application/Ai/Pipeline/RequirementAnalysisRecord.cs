using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiReap.Application.Persistence;
using AiReap.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiReap.Application.Ai.Pipeline;

// A candidate question the validator turned down, with every reason (ClarificationCandidateValidator).
public sealed record RejectedClarification(string Topic, string Question, string? Kind, IReadOnlyList<CandidateRejection> Reasons);

// What retrieval the analysis used. SourcePassages: the deterministic search of the source's own sentences
// that the answered-in-source check runs on ("lexical" - no embeddings). ProjectDocuments: semantic retrieval
// over the project's uploaded documents (KnowledgeRetriever) for each accepted question:
//   "used"          queries were sent; PassagesAttached passages scored at least the attach threshold;
//   "no-documents"  the project has no embedded documents, so nothing was searched;
//   "stale-only"    documents exist but none is embedded with the current model (re-index them);
//   "failed"        retrieval raised an error (Error); the questions were saved without document passages;
//   "not-run"       no question was accepted, so there was nothing to search for.
public sealed record AnalysisRetrievalReport(string SourcePassages, string ProjectDocuments, int Queries, int PassagesAttached, string? Error = null);

// Regeneration only: how the new run relates to the questions already saved for the source. Nothing
// existing is changed: RaisedAgain - existing questions a new candidate duplicated (so it was not saved);
// NotRaisedAgain - open AI questions no candidate of this run matched, for a reviewer to look at.
public sealed record AnalysisReconciliation(int ExistingQuestions, IReadOnlyList<string> RaisedAgain, IReadOnlyList<string> NotRaisedAgain);

// The persisted result of one analysis run, so a stored analysis can be shown again - and reused instead of
// re-sent to the AI - while the source text and analysis version are unchanged. Kept as an AIExecution row
// (OperationType RequirementAnalysisRecords.OperationType, InputReference = source id, OutputJson = this),
// next to the run's raw-response rows, so no schema change is needed. It holds no provider reasoning.
public sealed class RequirementAnalysisRecord
{
    public string AnalysisVersion { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public int SourceLength { get; set; }
    public DateTime AnalyzedAt { get; set; }
    public bool Regenerated { get; set; }
    public string Model { get; set; } = string.Empty;

    public List<string> Actors { get; set; } = new();
    public List<string> Capabilities { get; set; } = new();
    public List<string> DataElements { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public RequirementCompleteness? Completeness { get; set; }
    public AnalysisCoverageReport? Coverage { get; set; }
    public AnalysisRetrievalReport? Retrieval { get; set; }
    public AnalysisReconciliation? Reconciliation { get; set; }

    public List<Guid> AcceptedQuestionIds { get; set; } = new();
    public List<RejectedClarification> Rejected { get; set; } = new();
    public int SkippedDuplicates { get; set; }
}

public static class RequirementAnalysisRecords
{
    public const string OperationType = "RequirementAnalysisRecord";
    public const string RecordModel = "deterministic-validation";

    // Changes whenever the prompt or the acceptance rules change, which makes earlier records stale.
    public const string AnalysisVersion = RequirementAnalysisService.PromptTemplateVersion + "+" + ClarificationCandidateValidator.Version;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string SourceHash(string? text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty))).ToLowerInvariant();

    public static async Task<RequirementAnalysisRecord?> LatestAsync(IAiReapDbContext db, RequirementSource source, CancellationToken cancellationToken)
    {
        var reference = source.Id.ToString();
        var rows = await db.AIExecutions.AsNoTracking()
            .Where(e => e.ProjectId == source.ProjectId && e.OperationType == OperationType && e.InputReference == reference)
            .Select(e => new { e.Timestamp, e.OutputJson })
            .ToListAsync(cancellationToken);
        var latest = rows.OrderByDescending(r => r.Timestamp).FirstOrDefault();
        if (latest is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RequirementAnalysisRecord>(latest.OutputJson, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // An analysis that ran before records were kept: its raw-response audit rows exist, no record does.
    public static Task<bool> HasUnrecordedAnalysisAsync(IAiReapDbContext db, RequirementSource source, CancellationToken cancellationToken)
    {
        var reference = source.Id.ToString();
        return db.AIExecutions.AsNoTracking().AnyAsync(
            e => e.ProjectId == source.ProjectId && e.OperationType == "RequirementAnalysis" && e.InputReference == reference, cancellationToken);
    }

    public static IReadOnlyList<string> StaleReasons(RequirementAnalysisRecord record, RequirementSource source)
    {
        var reasons = new List<string>();
        if (record.SourceHash != SourceHash(source.RawText))
        {
            reasons.Add("The requirement source text has changed since this analysis ran.");
        }

        if (record.AnalysisVersion != AnalysisVersion)
        {
            reasons.Add($"The analysis rules have changed ({record.AnalysisVersion} -> {AnalysisVersion}).");
        }

        if (record.Coverage?.Status == AnalysisCoverageReport.Incomplete)
        {
            reasons.Add("This analysis did not cover the whole source.");
        }

        return reasons;
    }

    public static void Add(IAiReapDbContext db, RequirementSource source, RequirementAnalysisRecord record, string userId) =>
        db.AIExecutions.Add(new AIExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = source.ProjectId,
            OperationType = OperationType,
            UserId = userId,
            Timestamp = record.AnalyzedAt,
            Model = RecordModel,
            PromptTemplateVersion = record.AnalysisVersion,
            InputReference = source.Id.ToString(),
            OutputJson = JsonSerializer.Serialize(record)
        });
}
