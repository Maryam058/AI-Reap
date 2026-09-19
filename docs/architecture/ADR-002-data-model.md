# ADR-002: Generalized Artifact Data Model

Status: Accepted
Date: 2026-09-17

## Context

§29 of the source spec explicitly warns against "one table for every concept" and asks for a generalized model, justified. A literal reading of §29's entity list (FunctionalRequirement, NonFunctionalRequirement, BusinessRule, UserStory, AcceptanceCriterion, DesignArtifact, ApiSpecification, DataEntity, ImplementationTask, TestCase, ...) would produce 10+ near-identical tables, each re-implementing: an ID/code, a title, a status/approval workflow (§24), version history (§23), relationships to other artifacts (§21), and AI-vs-human origin tracking (§27, §32). That duplication is the exact anti-pattern §29 calls out.

## Decision

Model everything that flows through the approval/versioning/traceability pipeline as one polymorphic **`Artifact`** table with a shared "spine" of common columns, plus a JSON payload column for type-specific structured data (EF Core 8 native JSON column mapping via `OwnsOne(...).ToJson()`, deserialized into a C# discriminated type per `ArtifactType` at the Application layer — so application code stays strongly typed even though the DB column is JSON).

### `Artifact` (the spine — shared by every generated artifact type)

| Column | Notes |
|---|---|
| `Id` (Guid, PK) | |
| `ProjectId` (FK) | every artifact belongs to exactly one project (§5, §22 multi-project isolation) |
| `ArtifactType` (enum) | `FunctionalRequirement, NonFunctionalRequirement, BusinessRule, UserStory, AcceptanceCriterion, ClarificationQuestion, DesignArtifact, ApiSpecification, DataEntity, ImplementationTask, TestCase` |
| `Code` (string) | human-facing ID, e.g. `FR-001`, `TC-014`, unique per project+type |
| `Title` (string) | |
| `Priority` (enum, nullable) | promoted out of JSON because it's filtered/sorted on across nearly every type |
| `Status` (enum) | the §24 workflow: `AiGenerated, Draft, UnderReview, Approved, Implemented, Verified` (plus `Rejected`) |
| `Origin` (enum) | `Ai` \| `Human` — required by §32/§27, drives the "AI Generated — Human Review Required" UI badge |
| `RequirementSourceId` (FK, nullable) | which raw input (§6) this was derived from, if any |
| `DataJson` | type-specific fields (see below), mapped as an owned JSON column |
| `CurrentVersion` (int) | denormalized pointer to the latest `ArtifactVersion` |
| `CreatedByUserId`, `CreatedAt`, `UpdatedByUserId`, `UpdatedAt` | |

**Fields that stay inside `DataJson` per type** (never queried/filtered on directly, so no cost to keeping them flexible):

- FunctionalRequirement: Actor, Preconditions, Inputs, Processing, ExpectedResult, Dependencies
- NonFunctionalRequirement: Category, Description, TargetValue, `AssumptionStatus: "confirmed" | "proposed_assumption"` (hard-enforced per §32/REAP-045/REAP-011)
- BusinessRule: Statement
- UserStory: Persona, ValueStatement
- AcceptanceCriterion: Given, When, Then, Kind (`positive|negative|boundary`)
- ClarificationQuestion: Question, Reason, `ClarificationStatus: Open|Answered|Resolved|NotApplicable`, Answer
- DesignArtifact: free-form structured sections (architecture, modules, integrations, auth, jobs, caching, logging, deployment)
- ApiSpecification: Method, Route, RequestShape, ResponseShape, Validation, AuthorizationPolicy
- DataEntity: Fields[], Keys[], Relationships[], Indexes[]
- ImplementationTask: Description, TaskType, Dependencies, SuggestedRole, Estimate
- TestCase: Preconditions, Steps[], ExpectedResult, TestKind (`positive|negative|boundary|permission|validation`)

### Supporting generalized tables (replace what would otherwise be many join/history tables)

- **`ArtifactVersion`** — `ArtifactId, VersionNumber, DataSnapshotJson, ChangedByUserId, ChangedAt, Reason, Origin (Ai|Human)`. One table gives every artifact type version history + diff/compare (§23) for free.
- **`ArtifactRelationship`** — `SourceArtifactId, TargetArtifactId, RelationshipType, CreatedAt`. `RelationshipType` enum: `DerivedFrom, Implements, TestedBy, DependsOn, ConflictsWith, DuplicateOf, LinkedRule`. This single table **is** the traceability graph (§21: Objective→Requirement→Story→AC→Design→Task→Test is just a chain of `RelationshipType` edges), the business-rule-to-requirement link (§11), and the duplicate/conflict record (§15) — no separate `TraceabilityMatrix` or `ConflictReport` table needed; both are queries over this table.
- **`ArtifactReview`** — `ArtifactId, ReviewerUserId, Decision (Approved|Rejected|CommentOnly), Comment, ReviewedAt`. Generalized approval trail (§24) for any artifact type.
- **`AIExecution`** — `Id, ProjectId, OperationType, UserId, Timestamp, Model, PromptTemplateVersion, InputReference, OutputJson, Accepted (bool?), ProducedArtifactId (nullable FK)`. Full audit trail (§27). Explicitly **no chain-of-thought field** — only structured input/output, per the spec's hard prohibition.
- **`RequirementSource`** — raw retained input (§6): `Id, ProjectId, SourceType (Manual|Paste|FileUpload), RawText, OriginalFileName/Blob, CreatedAt`. Deliberately **not** an `Artifact` — it never goes through the approval/versioning workflow; it's the immutable thing artifacts are derived *from*.
- **`Document` / `DocumentChunk`** — RAG pipeline (§26): `Document(Id, ProjectId, FileName, ...)`, `DocumentChunk(Id, DocumentId, ChunkText, Embedding, ChunkIndex)`. Kept separate from `RequirementSource` because chunks serve semantic retrieval, not requirement derivation, and have a different lifecycle (re-chunked/re-embedded independently).
- **`Project` / `ProjectStakeholder`**, **`User` / `Role`** — conventional, not generalized (they aren't "artifacts" in the SDLC-pipeline sense).

### What this replaces

Without generalization, §29's list implies separate tables for: FunctionalRequirement, NonFunctionalRequirement, BusinessRule, UserStory, AcceptanceCriterion, ClarificationQuestion, DesignArtifact, ApiSpecification, DataEntity, ImplementationTask, TestCase — each needing its own version-history table, its own approval table, and its own side of every relationship join table (Requirement↔Story, Story↔AC, Requirement↔Task, Task↔Test, etc.). That's 10 primary tables + ~10 version tables + ~10 approval tables + a combinatorial number of join tables. The generalized model above collapses all of that into **`Artifact` + `ArtifactVersion` + `ArtifactRelationship` + `ArtifactReview`** — 4 tables carrying every artifact type, plus the genuinely distinct entities (`RequirementSource`, `Document`/`DocumentChunk`, `AIExecution`, `Project`/`ProjectStakeholder`, `User`/`Role`).

### Trade-off acknowledged

JSON-column fields are less queryable/indexable than real columns. Mitigation: only fields that are *never* filtered/sorted/joined on in the UI go into `DataJson` (verified against every generator spec in §9–§20); anything used for list filtering (`Priority`, `Status`, `ArtifactType`, `Code`) is a real column. If a specific `DataJson` field later needs indexed querying, EF Core supports computed/persisted columns over JSON paths — that's an additive migration, not a redesign.

## Consequences

- New artifact types (e.g. a future "Risk" or "Assumption" register) are a new `ArtifactType` enum value + a new payload shape — no new table, no new migration for relationships/versioning/approval.
- Traceability, impact analysis (§22), and conflict detection (§15) are all graph queries over one `ArtifactRelationship` table, which is exactly what `TraceabilityService` and `ImpactAnalysisService` (§30) need.
- Risk: a single wide `Artifact` table becomes a hot table under heavy load. Not a concern at this project's scale (single-tenant, project-scoped data); revisit only if it becomes one.
