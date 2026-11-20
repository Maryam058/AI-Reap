# AI-REAP — Requirements Traceability Matrix

This matrix assigns a stable internal ID to every requirement/feature extracted from the source PDF (`AI_Requirements_Engineering_SDLC_Automation_Project.pdf`), so that later design docs, code, PRs, and test cases can reference `REAP-xxx` instead of re-quoting the PDF. Update the **Status** column as work progresses — this file is the living tracker; `docs/roadmap/ROADMAP.md` is the time-ordered plan derived from it.

**Status legend:** `Not Started` · `In Progress` · `Built` · `Verified` (verified = covered by the §37 end-to-end demo or an automated test)

Legend for **Phase**: `P1` MVP, `P2` AI SDLC, `P3` Advanced Intelligence, `P4` Agentic (optional).

## A. Foundation & Cross-Cutting

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-001 | React + TypeScript frontend, responsive, reusable components | §2 | P1 | Must | Built (Vite scaffold; login/register/projects/detail pages; responsiveness not yet audited) |
| REAP-002 | ASP.NET Core Web API, C#, clean/layered architecture, DI, EF Core, Swagger | §2 | P1 | Must | Built & Verified |
| REAP-003 | SQL Server as system of record for all listed entities | §2, §29 | P1 | Must | Built & Verified (migration applied to real SQL Server; Project/Identity tables exercised, others migrated but unused so far — Phase-0 note; all core entities are exercised by later phases) |
| REAP-004 | AI provider abstraction (swappable without business-logic rewrite) | §2, §30 | P1 | Must | Built & Verified (Stub verified live; Anthropic implementation written, not yet exercised with a real key. Current: Gemini is the primary/default provider with Groq as automatic fallback (`FallbackAiChatClient`); the development stub is used in Development/Testing when no key is configured; Anthropic remains selectable but is not the default — `AiChatClientSelectionTests`, `FallbackAiChatClientTests`) |
| REAP-005 | Structured JSON output contract + response validation before persistence | §31 | P1 | Must | Built & Verified (`AiJsonParser` rejects the whole response — nothing partial persisted — on any shape mismatch; exercised across all four pipeline stages) |
| REAP-006 | Authentication (login/session) | §4, §33, §38 | P1 | Must | Built & Verified (2026-09-20: bootstrap admin, per-request token re-validation, deactivation) |
| REAP-007 | Role-based authorization enforced server-side (5 roles) | §4, §38 | P1 | Must | Built & Verified (403/200/401 confirmed live; 2026-09-20: public sign-up can no longer choose a role; Administrator user management at `/api/users`; Developer visibility: requirement-side artifacts only when Approved-or-later, while technical design/API/data-entity/implementation-task artifacts stay visible for §17 technical review; Developers cannot read raw requirement sources/documents or raw-source Copilot excerpts — `DeveloperArtifactVisibilityTests`, `RawSourceAccessTests`; Administrator Settings page is Administrator-only and read-only — `SystemSettingsTests`) |
| REAP-008 | Generalized/justified artifact data model (not 1 table per concept) | §29 | P1 | Must | Built (schema migrated; not yet exercised by an endpoint — Phase 1; since exercised by the artifact endpoints from Phase 1 onward) |
| REAP-009 | AI Audit Trail (op type, project, user, timestamp, model, prompt version, input ref, output, accept/reject, human edits, final version) | §27 | P3 | Should | In Progress — `GET /api/projects/{id}/ai-executions` + `AuditTrailPanel` UI now expose the full history live. `PromptTemplateVersion` (a versioned name per generation stage, e.g. `RequirementGeneration-v2`) and `Accepted` (set when a human approves/rejects the produced artifact) are now both wired — `AuditTrailFieldsTests`. **Remaining gap:** the human edit / final version is not yet linked back to the originating `AIExecution` |
| REAP-010 | Prohibit storing/exposing hidden provider chain-of-thought | §27, §32 | P1 (policy) | Must | Built (by design — `AIExecution` has no such field) |
| REAP-011 | AI safety rule: no fabricated confirmed requirements; missing info → question or labeled assumption | §32 | P1 | Must | In Progress (enforced via system prompt in the demo endpoint; schema-level `AssumptionStatus` enforcement is Phase 1 — since built, see REAP-045) |
| REAP-012 | Exception handling, logging, clean source control, setup/architecture docs | §38 | P1–P3 | Must | In Progress (default ASP.NET Core logging only; no custom exception middleware yet; no git repo initialized yet) |
| REAP-013 | Dev process rule: human must explain/verify every AI-generated diff before commit | §39 | Process | Must | N/A (process) |

## B. Project Management

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-020 | Create/manage projects (name, description, business problem, objectives, scope, domain, target users, stakeholders, tech prefs, constraints, timeline, status) | §5 | P1 | Must | Built & Verified — create/list/get/update (`PUT`) all fields, plus stakeholders (REAP-023), confirmed live |
| REAP-021 | Project status lifecycle: Draft→Requirements Gathering→Analysis→Design→Approved→Implementation→Testing→Completed | §5 | P1 | Must | Built & Verified — the original linear `PATCH /status` check (400 on skip/backward) is superseded by the lifecycle workflow: Draft→Requirements Gathering→Analysis→Design→Approved→Implementation→Testing→Completed with per-stage ownership; Design→Approved and Testing→Completed are Reviewer-gated requests (requester and approver must be different users; rejection requires a reason); evidence checks apply; Administrator override and rollback each require a reason; every transition is recorded in lifecycle history; skipping/backward moves are rejected with 409 (wrong role 403, missing reason 400) — `LifecycleController`, `LifecycleApiTests`, `ProjectLifecycleServiceTests` |
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
| REAP-042 | Clarification question lifecycle: Open/Answered/Resolved/Not Applicable | §8 | P1 | Must | Built & Verified (Open→Answered/NotApplicable verified live; an Answered clarification can now transition to Resolved — Open/NotApplicable questions cannot — `ClarificationResolutionTests`) |
| REAP-043 | Functional Requirement Generator (ID, actor, priority, preconditions, inputs, processing, expected result, dependencies) | §9 | P1 | Must | Built & Verified |
| REAP-044 | Non-Functional Requirement Generator (Perf/Sec/Avail/Scale/Usability/Maint/Compliance/Observability) | §10 | P1 | Must | Built & Verified |
| REAP-045 | NFR numeric targets flagged as proposed assumption until human-confirmed | §10, §32 | P1 | Must | Built & Verified (`AssumptionStatus` is force-set server-side, not trusted from the model — confirmed live; target confirmation implemented: a BA or Reviewer — not an Administrator, and not the NFR's author/current-version author — confirms against the current version, which creates a new human version; confirmation is separate from artifact approval and is not an Analysis-completion gate — `NfrConfirmationTests`) |
| REAP-046 | User Story Generator (persona, value, priority, dependencies, links, status) | §12 | P1 | Must | Built & Verified (links to FR via `ArtifactRelationship(DerivedFrom)`, confirmed live; 2026-09-28: manual story→story dependencies via `POST`/`DELETE /api/artifacts/{id}/dependencies` as `DependsOn` — BA/Administrator only, same-project user stories, duplicates and cycles rejected, no version/approval change, followed by impact analysis — `StoryDependencyTests`. Not included: AI-suggested dependencies, dependency-conflict detection, story sequencing) |
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
| REAP-072 | AI Impact Analysis on approved-requirement change; never silently modify downstream artifacts | §22 | P3 | Must | Built & Verified — `ImpactAnalysisService` does a directed downstream walk (max 3 hops) over the relationship graph from any artifact (upstream artifacts are never reported), splitting results into already-approved/implemented/verified vs. not; read-only, never mutates. `GET /api/artifacts/{id}/impact`, surfaced in `ArtifactDetails`. Verified live. When approved content changes, persistent impact notices are raised on the downstream artifacts (acknowledgeable, never auto-modifying them) — `Phase3TraceabilityTests`. |
| REAP-073 | Requirement Versioning (previous/new content, changed-by, date, reason, AI-vs-human origin) + version compare UI | §23 | P3 | Must | Built & Verified — version history existed since Phase 1 (`GET /api/artifacts/{id}/versions`); the UI (`ArtifactDetails`) renders it with AI/Human origin badges plus a two-version side-by-side field diff (client-side, changed fields highlighted), confirmed live against a real edit. |
| REAP-074 | Human-in-the-loop approval workflow (AI Generated→Draft→Under Review→Approved→Implemented→Verified) + visible AI badge | §24 | P1–P2 | Must | Built & Verified — AiGenerated/Approved/Rejected wired end-to-end incl. `ArtifactReview` record and UI badge; Implemented/Verified are derived from persisted work records, not from artifact approval: approved ImplementationTasks accept Developer completion/reopen records and approved TestCases accept QA execution outcomes; an FR becomes Implemented when every applicable linked non-rejected task's latest outcome is complete, and Verified when every applicable linked test has passed. Only Developer task outcomes and QA test outcomes count as evidence. No automatic demotion; rejection requires a reason; editing approved content returns it to UnderReview — `ArtifactWorkRecordTests`, `RejectionReasonTests`, `ArtifactApprovalIntegrityTests` |

## H. Intelligence Layer (Phase 3)

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-080 | AI Project Copilot (grounded Q&A over project data) | §25 | P3 | Should | Built & Verified — `CopilotService`, grounded in structured facts (untested FRs, open clarifications, pending review, recent changes) + document semantic search; verified live end-to-end against a real SQL Server |
| REAP-081 | RAG pipeline: extraction→chunking→embeddings→vector store→retrieval→LLM | §26 | P3 | Should | Built & Verified — extraction covers TXT/PDF/DOCX (shared `IDocumentTextExtractor` with Epic 2.4); embedding provider = `gemini-embedding-001` when a Gemini key is configured, else OpenAI `text-embedding-3-small` when its key is configured, else the deterministic stub; "vector store" is in-process cosine similarity over `DocumentChunk.Embedding`, not a separate store — documented as an adequate, non-premature choice at this scale |
| REAP-082 | Answers cite project source material where applicable | §26 | P3 | Should | Built & Verified — `CopilotAnswerResponse.Citations` includes document name, chunk index, and snippet for every cited excerpt |
| REAP-083 | Dashboard (req counts, FR/NFR split, stories, unresolved Qs, conflicts, approval progress, test coverage, review-needed, recent changes) | §28 | P1 (basic) / P3 (full) | Should | Built & Verified — counts, FR/NFR split, stories, open clarifications, approval split, recent changes (live updating), recorded conflicts (`DashboardConflictCountTests`), **test coverage** (`TestedFunctionalRequirementCount` / `FunctionalRequirementCount`: FRs with ≥1 `TestedBy` test case over all FRs, rejected included) and **requirements needing review** (`RequirementsNeedingReviewCount`: FRs + NFRs in AiGenerated/Draft/UnderReview; `PendingReviewCount` unchanged). Both are project-wide aggregates, identical for every role (`DashboardCoverageAndReviewTests`, `DeveloperArtifactVisibilityTests`) |

## I. Agentic SDLC (Phase 4 — optional)

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-090 | Requirements Agent → Analysis Agent → Architecture Agent → Dev Planning Agent → QA Agent → Review Agent, chained with structured I/O | §36 | P4 | Optional | Done (2026-09-19) |
| REAP-091 | Defined agent responsibilities + human approval boundaries per agent | §36 | P4 | Optional | Done (2026-09-19) |
| REAP-092 | Autonomous code deployment explicitly OUT of scope | §36 | — | Excluded | N/A — enforced structurally (no deploy agent; ADR-003 §7) |

## J. Acceptance / Demo Gate

| ID | Requirement | Source | Phase | Priority | Status |
|---|---|---|---|---|---|
| REAP-100 | End-to-end demo: complaint-management scenario run through full pipeline | §37 | Gate (post-P2) | Must | Done (2026-09-22) — `Section37DemoTests.Complaint_management_scenario_runs_end_to_end_then_a_changed_requirement_shows_correct_impact`, run against a real SQL Server |
| REAP-101 | Demo must show downstream impact analysis after changing a requirement | §37, §22 | Gate (post-P3) | Must | Done (2026-09-22) — same test: edits an approved FR, then asserts `/impact` names the actual linked user story/task/test-case ids, not just a non-empty/non-mutating response |
| REAP-102 | Definition of Done checklist satisfied (§38, full list) | §38 | Gate (release) | Must | Built & Verified (2026-09-23) — see the Release Gate checklist in `docs/roadmap/ROADMAP.md`, every item checked off against a passing `dotnet test AiReap.sln` (47/47) and a clean `npm run build`/`lint`. One standing caveat carried through, not hidden: no real-browser visual walkthrough of the UI has been done in this environment, only API-contract + type-checked/built-frontend verification |

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
