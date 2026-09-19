using AiReap.Domain.Enums;

namespace AiReap.Application.RequirementSources;

public record CreateRequirementSourceRequest(RequirementSourceType SourceType, string RawText, string? OriginalFileName);

public record RequirementSourceResponse(
    Guid Id,
    Guid ProjectId,
    RequirementSourceType SourceType,
    string RawText,
    string? OriginalFileName,
    DateTime CreatedAt);
