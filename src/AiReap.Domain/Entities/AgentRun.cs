using AiReap.Domain.Enums;

namespace AiReap.Domain.Entities;

// §36/REAP-090 — one execution of the agent chain against a requirement source. The run only
// advances when a human approves the current stage (REAP-091); it never advances on its own.
public class AgentRun
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid RequirementSourceId { get; set; }

    public AgentRunStatus Status { get; set; }

    // The project's lifecycle stage when the run started. Agents may draft ahead of the current
    // stage; this records that they did. A run never changes Project.Status. Null on older runs.
    public ProjectStatus? ProjectStatusAtStart { get; set; }
    public string StartedByUserId { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public List<AgentStageRun> Stages { get; set; } = new();
}
