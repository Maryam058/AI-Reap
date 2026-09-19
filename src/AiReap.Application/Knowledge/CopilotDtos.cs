namespace AiReap.Application.Knowledge;

public record AskCopilotRequest(string Question);

public record CopilotCitation(string DocumentName, int ChunkIndex, string Snippet);

public record CopilotAnswerResponse(
    string Answer,
    IReadOnlyList<CopilotCitation> Citations,
    IReadOnlyList<string> RelatedArtifactCodes);
