using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// A batched (resumable) AI analysis or requirement generation of one requirement source that is too
// large for the per-request budget of a configured provider (Groq). Start freezes the source and
// context fingerprints, the chunk plan and each part's prompt; Continue sends a bounded number of
// pending parts and keeps every finished part; Commit creates all artifacts in one save. Nothing
// becomes an artifact before Commit. See AiChunkRunService.
public class AiChunkRun
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid RequirementSourceId { get; set; }

    public AiChunkRunOperation Operation { get; set; }
    public AiChunkRunStatus Status { get; set; }

    public string PromptTemplateVersion { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;

    // SHA-256 (hex) of the source text, of the context that must not change during the run (prompt
    // version, system prompt, business objectives, clarifications), and of the chunk plan.
    public string SourceHash { get; set; } = string.Empty;
    public string ContextHash { get; set; } = string.Empty;
    public string PlanHash { get; set; } = string.Empty;

    // Objectives + document excerpts as carried by every part, frozen at Start (the excerpt
    // retrieval runs once).
    public string FixedContext { get; set; } = string.Empty;

    // Budget provider, input budget, part size, objective codes, excerpt references, source IDs.
    public string PlanJson { get; set; } = "{}";

    public int PartCount { get; set; }

    // Held by one Continue at a time.
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }

    public string? LastError { get; set; }

    // After Commit: created artifact ids and counts, so a repeated Commit returns the same result.
    public string? ResultJson { get; set; }

    public string StartedByUserId { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public List<AiChunkRunPart> Parts { get; set; } = new();
}

public class AiChunkRunPart
{
    public Guid Id { get; set; }
    public Guid AiChunkRunId { get; set; }
    public AiChunkRun? AiChunkRun { get; set; }

    public int PartNumber { get; set; }
    public string ChunkHash { get; set; } = string.Empty;
    public string? SectionPath { get; set; }

    // Requirement IDs (SrsIdExtractor) present in this part's source text, as a JSON array.
    public string SourceIdsJson { get; set; } = "[]";

    // The exact user prompt sent for this part; cleared when the run is committed, abandoned or
    // superseded (it quotes the source text).
    public string? PromptText { get; set; }

    public AiChunkRunPartStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Model { get; set; }

    // The validated response; RawResponse moves into the AI audit trail at Commit, ParsedJson is
    // cleared with PromptText.
    public string? RawResponse { get; set; }
    public string? ParsedJson { get; set; }

    public string? LastError { get; set; }
    public DateTime? CompletedAt { get; set; }

    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
