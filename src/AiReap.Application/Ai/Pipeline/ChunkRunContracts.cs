using AiReap.Domain.Entities;
using AiReap.Domain.Enums;

namespace AiReap.Application.Ai.Pipeline;

// Ai:Groq:MaxBatchedRunParts. 0 (default) = batch mode off: batched runs cannot be started,
// continued or committed, the AiChunkRuns tables are never queried, and normal Analyze/Generate
// behave exactly as before (a source needing more than Ai:Groq:MaxChunkedParts parts is refused).
// N > 0: a batched run may have at most N parts in total; each Continue sends at most the serving
// budget's MaxChunkedParts parts (or N when that limit is 0).
public interface IAiBatchSettings
{
    int MaxBatchedRunParts { get; }
}

public static class AiBatchSettingsExtensions
{
    public static bool BatchModeEnabled(this IAiBatchSettings settings) => settings.MaxBatchedRunParts > 0;
}

// What one operation (analysis or generation) contributes to a batched run. Implemented by the
// services that own the operation, so a batched run builds the same prompt, applies the same
// validation and persists exactly as the normal path does.
public interface IChunkRunOperation
{
    AiChunkRunOperation Operation { get; }

    // The prompt pieces a run is planned from. Generation retrieves project document excerpts here
    // (one embedding call, as a normal run does); the result is frozen into the run.
    Task<ChunkRunInputs> BuildRunInputsAsync(RequirementSource source, CancellationToken cancellationToken);

    // The text the run's ContextHash covers, rebuilt from the current database without any provider
    // call: what may not change between Start and Commit (prompt version, system prompt, business
    // objectives, clarifications). Document excerpts are frozen at Start and not re-checked.
    Task<string> ContextMaterialAsync(RequirementSource source, CancellationToken cancellationToken);

    // Sends one frozen part with the normal part rules (validation, one repair, one short rate-limit
    // wait). Returns the raw response, its validated form as JSON, and the model that served it.
    Task<ChunkRunPartResult> SendPartAsync(ChunkRunPartRequest request, CancellationToken cancellationToken);

    // Merges the parts and creates the artifacts exactly as the normal path does, in one save
    // (ArtifactPersistence). onPrepared runs inside that save's preparation, so the run's own
    // status change is written in the same SaveChanges.
    Task<ChunkRunCommitSummary> CommitAsync(
        RequirementSource source, ChunkRunFrozen frozen, IReadOnlyList<ChunkRunPartOutput> parts,
        Action<ChunkRunCommitSummary> onPrepared, CancellationToken cancellationToken);
}

// ContextMaterial: see IChunkRunOperation.ContextMaterialAsync.
public sealed record ChunkRunInputs(
    string PromptTemplateVersion,
    string SystemPrompt,
    string SourceText,
    string FixedContext,
    GenerationSupport.SharedContext Shared,
    IReadOnlyList<string> KnownCodes,
    IReadOnlyList<string> KnownReferences,
    bool ValidateSourceIds,
    string ContextMaterial);

// What a run keeps from Start for every later step.
public sealed record ChunkRunFrozen(
    Guid RunId,
    string SystemPrompt,
    IReadOnlyList<string> KnownCodes,
    IReadOnlyList<string> KnownReferences,
    bool ValidateSourceIds,
    IReadOnlyList<string> SourceIds,
    string SourceText);

public sealed record ChunkRunPartRequest(
    ChunkRunFrozen Frozen, Guid RequirementSourceId, int Number, int Count, string Prompt,
    IReadOnlyList<string> PartSourceIds, Func<TimeSpan, CancellationToken, Task>? Delay);

public sealed record ChunkRunPartResult(string RawResponse, string ParsedJson, string Model);

public sealed record ChunkRunPartOutput(int Number, int Count, string RawResponse, string ParsedJson, string Model, string? Section);

// SourceIdCoverage (P0-4): requirement generation's coverage after the save; null for analysis.
public sealed record ChunkRunCommitSummary(IReadOnlyList<Guid> CreatedArtifactIds, int SkippedDuplicates, SourceIdCoverage? SourceIdCoverage = null);
