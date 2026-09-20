using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

public class AgentStageRun
{
    public Guid Id { get; set; }
    public Guid AgentRunId { get; set; }
    public AgentRun? AgentRun { get; set; }

    public AgentKind Agent { get; set; }
    public int Order { get; set; }
    public AgentStageStatus Status { get; set; }

    // The agent's structured output contract, serialized (summary, metrics, produced
    // artifact references, findings). Never provider reasoning — same rule as AIExecution.
    public string OutputJson { get; set; } = "{}";
    public string? Error { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public string? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionComment { get; set; }

    // Concurrency token: makes "approve / reject / retry this stage" a claim that only one
    // caller can win, so a double-click or two reviewers can't run the next stage twice.
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
