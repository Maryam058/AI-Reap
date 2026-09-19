namespace AiReap.Domain.Enums;

// §21 traceability, §11 rule-to-requirement links, §15 duplicate/conflict records —
// all modeled as edges in ArtifactRelationship rather than separate join tables.
public enum RelationshipType
{
    DerivedFrom,
    Implements,
    TestedBy,
    DependsOn,
    ConflictsWith,
    DuplicateOf,
    LinkedRule
}
