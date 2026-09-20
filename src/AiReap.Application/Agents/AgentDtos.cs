using AiReap.Domain.Enums;

namespace AiReap.Application.Agents;

public record AgentDefinitionResponse(
    AgentKind Agent,
    int Order,
    string DisplayName,
    string Responsibility,
    string Inputs,
    string Outputs,
    IReadOnlyList<string> ApproverRoles);

public record AgentStageResponse(
    Guid Id,
    AgentKind Agent,
    int Order,
    string DisplayName,
    AgentStageStatus Status,
    AgentOutput? Output,
    string? Error,
    IReadOnlyList<string> ApproverRoles,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? DecidedByUserId,
    DateTime? DecidedAt,
    string? DecisionComment);

public record AgentRunResponse(
    Guid Id,
    Guid ProjectId,
    Guid RequirementSourceId,
    AgentRunStatus Status,
    string StartedByUserId,
    DateTime StartedAt,
    DateTime? CompletedAt,
    IReadOnlyList<AgentStageResponse> Stages);

public record StartAgentRunRequest(Guid RequirementSourceId);

public record AgentDecisionRequest(bool Approve, string? Comment);
