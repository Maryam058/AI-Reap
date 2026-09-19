# AI-REAP — Requirements Traceability Matrix

This matrix assigns a stable internal ID to every requirement/feature extracted from the source PDF (`AI_Requirements_Engineering_SDLC_Automation_Project.pdf`), so that later design docs, code, PRs, and test cases can reference `REAP-xxx` instead of re-quoting the PDF. Update the **Status** column as work progresses — this file is the living tracker; `docs/roadmap/ROADMAP.md` is the time-ordered plan derived from it.

**Status legend:** `Not Started` · `In Progress` · `Built` · `Verified` (verified = covered by the §37 end-to-end demo or an automated test)

Legend for **Phase**: `P1` MVP, `P2` AI SDLC, `P3` Advanced Intelligence, `P4` Agentic (optional).

## A. Foundation & Cross-Cutting

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-001 | React + TypeScript frontend, responsive, reusable components | §2 | P1 | Must | Built (Vite scaffold; login/register/projects/detail pages; responsiveness not yet audited) |
| REAP-002 | ASP.NET Core Web API, C#, clean/layered architecture, DI, EF Core, Swagger | §2 | P1 | Must | Built & Verified |
| REAP-003 | SQL Server as system of record for all listed entities | §2, §29 | P1 | Must | Built & Verified (migration applied to real SQL Server; Project/Identity tables exercised, others migrated but unused so far) |
| REAP-004 | AI provider abstraction (swappable without business-logic rewrite) | §2, §30 | P1 | Must | Built & Verified (Stub verified live; Anthropic implementation written, not yet exercised with a real key) |
| REAP-005 | Structured JSON output contract + response validation before persistence | §31 | P1 | Must | Built & Verified (`AiJsonParser` rejects the whole response — nothing partial persisted — on any shape mismatch; exercised across all four pipeline stages) |
| REAP-006 | Authentication (login/session) | §4, §33, §38 | P1 | Must | Built & Verified |
| REAP-007 | Role-based authorization enforced server-side (5 roles) | §4, §38 | P1 | Must | Built & Verified (403/200/401 confirmed live) |
| REAP-008 | Generalized/justified artifact data model (not 1 table per concept) | §29 | P1 | Must | Built (schema migrated; not yet exercised by an endpoint — Phase 1) |
| REAP-009 | AI Audit Trail (op type, project, user, timestamp, model, prompt version, input ref, output, accept/reject, human edits, final version) | §27 | P3 | Should | In Progress — `GET /api/projects/{id}/ai-executions` + `AuditTrailPanel` UI now expose the full history live. Two known gaps: `PromptTemplateVersion` is never populated (system prompts are inline C# constants, not a versioned template store) and `Accepted`/human-edit linkage isn't wired (one `AIExecution` can produce many artifacts, so the single `ProducedArtifactId` FK on the entity doesn't cleanly capture "accepted" for a batch operation) |
| REAP-010 | Prohibit storing/exposing hidden provider chain-of-thought | §27, §32 | P1 (policy) | Must | Built (by design — `AIExecution` has no such field) |
| REAP-011 | AI safety rule: no fabricated confirmed requirements; missing info → question or labeled assumption | §32 | P1 | Must | In Progress (enforced via system prompt in the demo endpoint; schema-level `AssumptionStatus` enforcement is Phase 1) |
| REAP-012 | Exception handling, logging, clean source control, setup/architecture docs | §38 | P1–P3 | Must | In Progress (default ASP.NET Core logging only; no custom exception middleware yet; no git repo initialized yet) |
| REAP-013 | Dev process rule: human must explain/verify every AI-generated diff before commit | §39 | Process | Must | N/A (process) |

## B. Project Management

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-020 | Create/manage projects (name, description, business problem, objectives, scope, domain, target users, stakeholders, tech prefs, constraints, timeline, status) | §5 | P1 | Must | Built & Verified — create/list/get/update (`PUT`) all fields, plus stakeholders (REAP-023), confirmed live |
| REAP-021 | Project status lifecycle: Draft→Requirements Gathering→Analysis→Design→Approved→Implementation→Testing→Completed | §5 | P1 | Must | Built & Verified — `PATCH /status` now enforces the linear sequence (stay or advance exactly one stage; skip-ahead/backward rejected with 400), confirmed live |
| REAP-022 | Multiple independent projects, each its own AI/RAG context | §5, §38 | P1 | Must | Built & Verified (multi-project data isolation via ProjectId; RAG context isolation is Phase 3) |
| REAP-023 | Project stakeholders management | §5, §29 | P1 | Should | Built & Verified — full CRUD (`/api/projects/{id}/stakeholders`), `ProjectSettingsPanel` in the UI, role gating confirmed live |

## C. Requirement Intake

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-030 | Manual text entry / rich-text requirement description | §6 | P1 | Must | Built & Verified (`RequirementSourceType.Manual`; rich-text formatting not yet in the UI — plain textarea) |
| REAP-031 | Paste meeting notes | §6 | P1 | Must | Built & Verified (`RequirementSourceType.Paste`, same entry form) |
| REAP-032 | Upload TXT/PDF/DOCX documents | §6 | P1–P2 | Must | Built & Verified — TXT/PDF/DOCX all confirmed live via `IDocumentTextExtractor` (PdfPig + DocumentFormat.OpenXml, ADR-001 §4). PDF is text-layer only (no OCR); DOCX is paragraph text only |
| REAP-033 | Retain original source verbatim; AI content never overwrites input | §6, §32 | P1 | Must | Built & Verified (`RequirementSource.RawText` is immutable; all generated content lives in separate `Artifact` rows) |

## D. Core AI Analysis Pipeline

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-040 | AI Requirement Analyzer → actors, capabilities, data, rules, missing info | §7 | P1 | Must | Built & Verified (live end-to-end incl. real SQL Server) |
| REAP-041 | AI Clarification Engine (ambiguity/gaps/contradictions/etc. detection) | §8 | P1 | Must | Built & Verified (questions generated from `missingInformation`, linked to source via `RequirementSourceId`) |
| REAP-042 | Clarification question lifecycle: Open/Answered/Resolved/Not Applicable | §8 | P1 | Must | In Progress (Open→Answered/NotApplicable verified live; "Resolved" is a defined state but nothing transitions to it yet — no UI/endpoint concept of a separate resolution step beyond answering) |
| REAP-043 | Functional Requirement Generator (ID, actor, priority, preconditions, inputs, processing, expected result, dependencies) | §9 | P1 | Must | Built & Verified |
| REAP-044 | Non-Functional Requirement Generator (Perf/Sec/Avail/Scale/Usability/Maint/Compliance/Observability) | §10 | P1 | Must | Built & Verified |
| REAP-045 | NFR numeric targets flagged as proposed assumption until human-confirmed | §10, §32 | P1 | Must | Built & Verified (`AssumptionStatus` is force-set server-side, not trusted from the model — confirmed live) |
| REAP-046 | User Story Generator (persona, value, priority, dependencies, links, status) | §12 | P1 | Must | Built & Verified (links to FR via `ArtifactRelationship(DerivedFrom)`, confirmed live) |
| REAP-047 | Acceptance Criteria Generator (Given/When/Then; positive/negative/boundary) | §13 | P1 | Must | Built & Verified (per-story generation, linked via `DerivedFrom`, confirmed live) |

## E. Business Rules, Quality & Conflicts (Phase 2)

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-050 | Business Rule Extraction, linked to requirements | §11 | P2 | Must | Built & Verified (`BusinessRuleService`, linked via `ArtifactRelationship(LinkedRule)`, confirmed live) |
| REAP-051 | Requirement Quality Analysis (ambiguity, completeness, consistency, testability, etc.) | §14 | P2 | Must | Built & Verified (`RequirementQualityService` — ephemeral findings, not persisted as artifacts, logged via `AIExecution`) |
| REAP-052 | Duplicate & Conflict Detection with human-resolution-required outcome | §15 | P2 | Must | Built & Verified (`ConflictDetectionService`, project-wide scan over all FRs, recorded as `ArtifactRelationship(ConflictsWith\|DuplicateOf)`, never auto-resolved) |

## F. Design & Planning Assistants (Phase 2)

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-060 | AI Solution Design Assistant (architecture, modules, APIs, DB, integrations, auth, jobs, caching, logging, deployment) | §16 | P2 | Must | Built & Verified (`SolutionDesignService`, `DA-xxx` artifacts; APIs/DB modeled as separate artifact types per REAP-061/062) |
| REAP-061 | Database Design Assistant (entities, fields, types, keys, relationships, indexes) | §17 | P2 | Must | Built & Verified (`DatabaseDesignService`, `DE-xxx` artifacts linked `DerivedFrom` FR) |
| REAP-062 | API Design Assistant (contract: purpose, method, route, req/res, validation, authZ, linked req IDs) | §18 | P2 | Must | Built & Verified (`ApiDesignService`, `API-xxx` artifacts linked `DerivedFrom` FR) |
| REAP-063 | Implementation Planning / task generation (desc, type, priority, deps, related reqs, role, status, estimate) | §19 | P2 | Must | Built & Verified (`ImplementationPlanningService`, `TASK-xxx` artifacts linked `Implements` FR; status is the standard artifact workflow, not a separate field) |
| REAP-064 | All design output editable/reviewable, never auto-applied | §16, §17 | P2 | Must | Built & Verified (same generic `PATCH /api/artifacts/{id}` edit + approval workflow as every other artifact type) |

## G. QA & Traceability (Phase 2–3)

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-070 | AI Test Case Generator (positive/negative/boundary/permission/validation) | §20 | P2 | Must | Built & Verified (`TestGenerationService`, `TC-xxx` artifacts per FR, linked `TestedBy` from the FR) |
| REAP-071 | Requirements Traceability Matrix (Objective→Req→Story→AC→Design→Task→Test) | §21 | P2–P3 | Must | Built & Verified — `TraceabilityService.GetMatrixAsync`, `GET /api/projects/{id}/traceability-matrix`, one row per FR rolling up BusinessRules/UserStories/AcceptanceCriteria/DesignArtifacts/DataEntities/ApiSpecifications/ImplementationTasks/TestCases. `TraceabilityMatrixPanel` renders it as a table. Verified live with a full 7-artifact chain off one FR. |
| REAP-072 | AI Impact Analysis on approved-requirement change; never silently modify downstream artifacts | §22 | P3 | Must | Built & Verified — `ImpactAnalysisService` does an undirected BFS (3 hops) over the relationship graph from any artifact, splitting results into already-approved/implemented/verified vs. not; read-only, never mutates. `GET /api/artifacts/{id}/impact`, surfaced in `ArtifactDetails`. Verified live. |
| REAP-073 | Requirement Versioning (previous/new content, changed-by, date, reason, AI-vs-human origin) + version compare UI | §23 | P3 | Must | Built & Verified — version history existed since Phase 1 (`GET /api/artifacts/{id}/versions`); the UI (`ArtifactDetails`) renders it with AI/Human origin badges plus a two-version side-by-side field diff (client-side, changed fields highlighted), confirmed live against a real edit. |
| REAP-074 | Human-in-the-loop approval workflow (AI Generated→Draft→Under Review→Approved→Implemented→Verified) + visible AI badge | §24 | P1–P2 | Must | Built & Verified — AiGenerated/Approved/Rejected wired end-to-end incl. `ArtifactReview` record and UI badge; Implemented/Verified are now derived automatically (approving a Functional Requirement's last linked ImplementationTask promotes it to Implemented, approving its last linked TestCase promotes it to Verified), confirmed live including the partial-approval case staying at Implemented. **Remaining gap:** Draft/UnderReview transitions still aren't driven by any workflow (nothing currently moves an artifact into them); no demotion if a task/test is later un-approved after promotion — documented limitation, not silently glossed over |

## H. Intelligence Layer (Phase 3)

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-080 | AI Project Copilot (grounded Q&A over project data) | §25 | P3 | Should | Built & Verified — `CopilotService`, grounded in structured facts (untested FRs, open clarifications, pending review, recent changes) + document semantic search; verified live end-to-end against a real SQL Server |
| REAP-081 | RAG pipeline: extraction→chunking→embeddings→vector store→retrieval→LLM | §26 | P3 | Should | Built & Verified — extraction covers TXT/PDF/DOCX (shared `IDocumentTextExtractor` with Epic 2.4); embedding provider = OpenAI `text-embedding-3-small` (ADR-001 §2); "vector store" is in-process cosine similarity over `DocumentChunk.Embedding`, not a separate store — documented as an adequate, non-premature choice at this scale |
| REAP-082 | Answers cite project source material where applicable | §26 | P3 | Should | Built & Verified — `CopilotAnswerResponse.Citations` includes document name, chunk index, and snippet for every cited excerpt |
| REAP-083 | Dashboard (req counts, FR/NFR split, stories, unresolved Qs, conflicts, approval progress, test coverage, review-needed, recent changes) | §28 | P1 (basic) / P3 (full) | Should | Built & Verified for the P1 basic set (counts, approval split, recent changes — confirmed live updating in real time); conflicts and test coverage arrive with Phase 2/3 |

## I. Agentic SDLC (Phase 4 — optional)

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-090 | Requirements Agent → Analysis Agent → Architecture Agent → Dev Planning Agent → QA Agent → Review Agent, chained with structured I/O | §36 | P4 | Optional | Not Started |
| REAP-091 | Defined agent responsibilities + human approval boundaries per agent | §36 | P4 | Optional | Not Started |
| REAP-092 | Autonomous code deployment explicitly OUT of scope | §36 | — | Excluded | N/A |

## J. Acceptance / Demo Gate

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-100 | End-to-end demo: complaint-management scenario run through full pipeline | §37 | Gate (post-P2) | Must | Not Started |
| REAP-101 | Demo must show downstream impact analysis after changing a requirement | §37, §22 | Gate (post-P3) | Must | Not Started |
| REAP-102 | Definition of Done checklist satisfied (§38, full list) | §38 | Gate (release) | Must | Not Started |

---

## Coverage Check

Every numbered section of the PDF (1–39) maps to at least one row above:

- §1 Vision → framing in REQUIREMENTS-ANALYSIS.md §1 (no separate ID; governs all)
- §2 Stack → REAP-001..005
- §3 Workflow → structural backbone for REAP-040..074 (order of the pipeline)
- §4 Roles → REAP-007
- §5 Project Mgmt → REAP-020..023
- §6 Intake → REAP-030..033
- §7 Analyzer → REAP-040
- §8 Clarification → REAP-041..042
- §9 FR Generator → REAP-043
- §10 NFR Generator → REAP-044..045
- §11 Business Rules → REAP-050
- §12 User Stories → REAP-046
- §13 Acceptance Criteria → REAP-047
- §14 Quality Analysis → REAP-051
- §15 Conflict Detection → REAP-052
- §16 Solution Design → REAP-060, 064
- §17 DB Design → REAP-061
- §18 API Design → REAP-062
- §19 Implementation Planning → REAP-063
- §20 Test Cases → REAP-070
- §21 Traceability → REAP-071
- §22 Impact Analysis → REAP-072
- §23 Versioning → REAP-073
- §24 Approval Workflow → REAP-074
- §25 Copilot → REAP-080
- §26 RAG → REAP-081..082
- §27 Audit Trail → REAP-009..010
- §28 Dashboard → REAP-083
- §29 Data Model → REAP-008
- §30 AI Architecture → REAP-004
- §31 Structured Output → REAP-005
- §32 AI Safety → REAP-010..011, 045
- §33–36 Phasing → phase column across whole matrix
- §37 Demo → REAP-100..101
- §38 Definition of Done → REAP-102 (and rolled up across matrix)
- §39 Dev Rule → REAP-013

No section is unaccounted for.
