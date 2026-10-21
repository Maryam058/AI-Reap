namespace AiReap.Application.Knowledge;

public record AskCopilotRequest(string Question);

public record CopilotCitation(string DocumentName, int ChunkIndex, string Snippet);

// Context: what project context the answer was given, and what was intentionally shortened or left
// out (CopilotContextFitter). Response-only - never persisted. Null only from callers that build the
// response without it.
public record CopilotAnswerResponse(
    string Answer,
    IReadOnlyList<CopilotCitation> Citations,
    IReadOnlyList<string> RelatedArtifactCodes,
    CopilotContextReport? Context = null);

// The longest question the Copilot accepts (Ai:Copilot:MaxQuestionChars), for the question input.
public record CopilotLimitsResponse(int MaxQuestionChars);

// Every count describes the prompt that was actually sent for the answer, and only content the caller
// may see: artifacts are the caller's visible business objectives, functional and non-functional
// requirements, business rules, user stories and clarification questions; versions, impact items and
// excerpts are those the caller's view already contains.
//
//   ArtifactsFull       - shown with their complete content.
//   ArtifactsTruncated  - shown with only the first part of their content (marked "content shortened").
//   ArtifactsTitleOnly  - shown by code, type, status and title only.
//   ArtifactsOmitted    - not shown at all.
//     The four are mutually exclusive and add up to the number of such artifacts the caller can see.
//   VersionsOmitted     - edits in the version history (version 2 onwards of a visible artifact) not
//                         listed under RECENT CHANGES, including those beyond the standard 15 newest.
//   ImpactCodesOmitted  - downstream artifacts found by the impact analysis of an artifact code named
//                         in the question but not listed (the impact summary keeps their total).
//   ExcerptsOmitted     - document excerpts retrieved for the question (the top matches, at most 5)
//                         that were not included. Null when the caller may not read raw sources: no
//                         excerpts are retrieved for them and nothing about documents is reported.
//
// Reduced: any count above is non-zero. Reason: null when nothing was reduced; "standardLimits" when
// only the Copilot's fixed limits applied (they apply whichever provider serves the request);
// "providerBudget" when the context was further fitted to the input budget of the provider that
// serves the request - then Provider and InputBudget (input tokens, output reserve already set aside)
// name it.
public record CopilotContextReport(
    bool Reduced,
    string? Reason,
    string? Provider,
    int? InputBudget,
    int ArtifactsFull,
    int ArtifactsTruncated,
    int ArtifactsTitleOnly,
    int ArtifactsOmitted,
    int VersionsOmitted,
    int ImpactCodesOmitted,
    int? ExcerptsOmitted)
{
    public const string StandardLimits = "standardLimits";
    public const string ProviderBudget = "providerBudget";
}
