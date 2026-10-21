namespace AiReap.Application.Ai.Pipeline;

// A batched run as the API reports it. PartsPerAction: the most parts one Continue sends.
// LastErrorRetryable: the run is still Running, so Continue retries the failed part.
public sealed record AiChunkRunResponse(
    Guid Id,
    Guid RequirementSourceId,
    string Operation,
    string Status,
    int PartCount,
    int SucceededParts,
    int FailedParts,
    int PendingParts,
    int PartsPerAction,
    int RemainingActions,
    string? BudgetProvider,
    string? LastError,
    bool LastErrorRetryable,
    bool LeaseActive,
    DateTime StartedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    AiChunkRunResult? Result,
    IReadOnlyList<AiChunkRunPartSummary> Parts);

public sealed record AiChunkRunPartSummary(int Number, string Status, int Attempts, string? Section, string? Model, string? LastError);

// SourceIdCoverage (P0-4): set on the response to the Commit of a generation run, computed after its
// save; it is not stored with the run, so a later read shows null - the current coverage is
// GET /api/requirement-sources/{id}/source-id-coverage. Always null for analysis runs.
public sealed record AiChunkRunResult(IReadOnlyList<Guid> CreatedArtifactIds, int SkippedDuplicates, SourceIdCoverage? SourceIdCoverage = null);

// A request a batched run cannot take (400): Code is the API error code.
public sealed class AiBatchRequestException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;

    public const string BatchModeDisabled = "batch_mode_disabled";
    public const string NotNeeded = "ai_batch_not_needed";
    public const string TooManyParts = "ai_batch_too_many_parts";
    public const string CannotPlan = "ai_batch_cannot_plan";
}

// Start lost to (or found) an active run for the same source and operation (409).
public sealed class AiChunkRunActiveException(Guid activeRunId)
    : InvalidOperationException("A batched run of this operation is already active for this requirement source.")
{
    public Guid ActiveRunId { get; } = activeRunId;
}
