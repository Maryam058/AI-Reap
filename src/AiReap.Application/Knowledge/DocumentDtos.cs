namespace AiReap.Application.Knowledge;

public record DocumentResponse(
    Guid Id,
    Guid ProjectId,
    string FileName,
    string ContentType,
    DateTime UploadedAt,
    string UploadedByUserId,
    int ChunkCount);
