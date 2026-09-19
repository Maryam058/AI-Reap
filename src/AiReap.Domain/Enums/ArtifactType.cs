namespace AiReap.Domain.Enums;

// See docs/architecture/ADR-002-data-model.md — one Artifact table, discriminated by this enum,
// replaces a separate table per SDLC concept (§29).
public enum ArtifactType
{
    FunctionalRequirement,
    NonFunctionalRequirement,
    BusinessRule,
    UserStory,
    AcceptanceCriterion,
    ClarificationQuestion,
    DesignArtifact,
    ApiSpecification,
    DataEntity,
    ImplementationTask,
    TestCase
}
