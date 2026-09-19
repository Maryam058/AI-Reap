namespace AiReap.Application.Traceability;

// §21 — one row per functional requirement, rolling up everything traced to it through the
// ArtifactRelationship graph. A lighter-weight "matrix" than a full chain reconstruction —
// sufficient to answer "what traces back to FR-001?" without over-building a generic graph
// query engine nobody asked for.
public record TraceabilityRow(
    Guid FunctionalRequirementId,
    string FunctionalRequirementCode,
    string FunctionalRequirementTitle,
    IReadOnlyList<TraceRef> BusinessRules,
    IReadOnlyList<TraceRef> UserStories,
    IReadOnlyList<TraceRef> AcceptanceCriteria,
    IReadOnlyList<TraceRef> DesignArtifacts,
    IReadOnlyList<TraceRef> DataEntities,
    IReadOnlyList<TraceRef> ApiSpecifications,
    IReadOnlyList<TraceRef> ImplementationTasks,
    IReadOnlyList<TraceRef> TestCases);

public record TraceRef(Guid Id, string Code, string Title);

// §22 — advisory only; never mutates anything. Downstream artifacts grouped by whether
// they've already passed review, since those are the ones a change is most disruptive to.
public record ImpactAnalysisResult(
    Guid ArtifactId, string ArtifactCode, string ArtifactTitle,
    IReadOnlyList<ImpactedArtifact> ApprovedOrLaterDownstream,
    IReadOnlyList<ImpactedArtifact> OtherDownstream);

public record ImpactedArtifact(Guid Id, string Code, string Title, string ArtifactType, string Status, string RelationshipType);
