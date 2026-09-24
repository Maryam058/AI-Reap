namespace AiReap.Application.Traceability;

// §21 — one row per functional requirement, rolling up everything traced to it through the
// ArtifactRelationship graph (Business Objective -> Requirement -> Story -> AC -> Design -> Task
// -> Test). A lighter-weight "matrix" than a full chain reconstruction — sufficient to answer
// "what traces back to FR-001?" without over-building a generic graph query engine.
public record TraceabilityRow(
    Guid FunctionalRequirementId,
    string FunctionalRequirementCode,
    string FunctionalRequirementTitle,
    IReadOnlyList<TraceRef> BusinessObjectives,
    IReadOnlyList<TraceRef> BusinessRules,
    IReadOnlyList<TraceRef> UserStories,
    IReadOnlyList<TraceRef> AcceptanceCriteria,
    IReadOnlyList<TraceRef> DesignArtifacts,
    IReadOnlyList<TraceRef> DataEntities,
    IReadOnlyList<TraceRef> ApiSpecifications,
    IReadOnlyList<TraceRef> ImplementationTasks,
    IReadOnlyList<TraceRef> TestCases);

public record TraceRef(Guid Id, string Code, string Title);

// §21 — each objective and the requirements that serve it; an empty list is a coverage gap.
public record BusinessObjectiveResponse(Guid Id, string Code, string Title, IReadOnlyList<TraceRef> Requirements);

public record SetBusinessObjectivesRequest(IReadOnlyList<Guid> BusinessObjectiveIds);

// §22 — advisory only; never mutates anything. Downstream artifacts grouped by whether
// they've already passed review, since those are the ones a change is most disruptive to.
public record ImpactAnalysisResult(
    Guid ArtifactId, string ArtifactCode, string ArtifactTitle,
    IReadOnlyList<ImpactedArtifact> ApprovedOrLaterDownstream,
    IReadOnlyList<ImpactedArtifact> OtherDownstream);

// Path explains how the artifact is reached, e.g. "AC-001 derived from US-001 derived from FR-001".
public record ImpactedArtifact(Guid Id, string Code, string Title, string ArtifactType, string Status, string RelationshipType, string Path = "");

public record ImpactNoticeResponse(
    Guid Id,
    Guid SourceArtifactId, string SourceArtifactCode, string SourceArtifactTitle, int SourceVersion,
    Guid AffectedArtifactId, string AffectedArtifactCode, string AffectedArtifactTitle,
    AiReap.Domain.Enums.ArtifactType AffectedArtifactType, AiReap.Domain.Enums.ArtifactStatus AffectedArtifactStatus,
    string Path, string CreatedByUserId, DateTime CreatedAt,
    string? AcknowledgedByUserId, DateTime? AcknowledgedAt, string? AcknowledgementNote);

public record AcknowledgeImpactNoticeRequest(string? Note);
