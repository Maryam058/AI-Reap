# AI-REAP — Development Roadmap

Derived from `docs/requirements/REQUIREMENTS-ANALYSIS.md` and `docs/requirements/TRACEABILITY-MATRIX.md`. Phase boundaries follow the PDF's own phasing (§33–§36); each phase ends with a demonstrable, working increment — not partial/broken features.

Update this file's checkboxes and the matrix's Status column together as work lands; don't let them drift apart.

---

## Phase 0 — Project Setup & Architecture Decisions (pre-MVP) — ✅ Complete (2026-09-17)

Not explicitly a numbered phase in the PDF, but required before any code, because the PDF explicitly demands justified decisions (§29) rather than defaults.

- [x] Resolve the **Open Questions** in `REQUIREMENTS-ANALYSIS.md` (LLM provider, vector store, auth mechanism, file-parsing libs, hosting target, multi-tenancy scope) — recorded in `docs/architecture/ADR-001-technology-decisions.md`.
- [x] **Architecture Decision Record: generalized artifact data model** (REAP-008) — `docs/architecture/ADR-002-data-model.md`. Implemented as `Artifact`/`ArtifactVersion`/`ArtifactRelationship`/`ArtifactReview` in `AiReap.Domain`, migrated to SQL Server.
- [x] Scaffold solution: `AiReap.sln` with `AiReap.Api` / `AiReap.Application` / `AiReap.Domain` / `AiReap.Infrastructure` (ASP.NET Core 8, EF Core 8, Swagger), plus `web/` (React 19 + TypeScript, Vite).
- [x] Stand up `IAiChatClient` abstraction (REAP-004) with two implementations: `StubAiChatClient` (default, no key required) and `AnthropicAiChatClient` (used automatically once `Ai:Anthropic:ApiKey` is configured) — verified round-tripping through `/api/aidemo/analyze`.
- [x] Auth scaffolding (REAP-006) + role model (REAP-007): ASP.NET Identity + JWT, five roles seeded on startup (Administrator, BusinessAnalyst, Developer, QA, Reviewer). Verified: register/login issue a role-claimed JWT; a `Developer` gets `403` on `POST /api/projects`, `200` on `GET /api/projects`; no token gets `401`.
- [ ] CI basics (build + test on push) — deferred; no git remote/CI provider set up yet.

**Exit criteria — verified against a real SQL Server (Docker, throwaway container) end-to-end:** register → login → JWT issued with role claim → role-gated `POST /api/projects` succeeds for BusinessAnalyst/blocked for Developer → `GET /api/projects` lists it → `POST /api/aidemo/analyze` round-trips through `IAiChatClient` and logs an `AIExecution` audit row → Swagger documents all of it. React app builds, type-checks, and serves. See `docs/architecture/ADR-001`/`ADR-002` for the decisions behind this.

**Known Phase 0 gaps carried into Phase 1:** no automated test project yet; `RequirementSource`/`Document` entities exist in the schema but have no endpoints yet; self-registration lets a caller pick any role (fine for a solo dev demo, needs tightening once there's a real Administrator-only user management flow — see Epic 1.5/2.x). **Closed 2026-09-20** — see "Authentication & role management" below.

---

## Phase 1 — MVP (PDF §33, sections 1–13 are the "mandatory first-month deliverable")

Goal: raw requirement in → clarified, structured functional/non-functional requirements + stories + acceptance criteria out, all persisted, versioned-enough to demo, with visible AI-vs-human distinction.

### Epic 1.1 — Project Management (REAP-020..023) — ✅ done (2026-09-17)
- [x] Project CRUD — Create/List/GetById shipped in Phase 1; `PUT /api/projects/{id}` (edit all descriptive fields) added later to close the gap. Verified live.
- [x] Project status lifecycle enforcement — `UpdateStatusAsync` now rejects any transition except staying put or advancing exactly one stage in the fixed `Draft→...→Completed` sequence (400 with a clear message otherwise). No "reopen"/"reject back to draft" concept exists in the spec, so none was invented. Verified live: valid single-step transition succeeds, skip-ahead and backward attempts both correctly rejected.
- [x] Stakeholder management — full CRUD (`POST`/`GET`/`PUT`/`DELETE` under `/api/projects/{id}/stakeholders`), `ProjectSettingsPanel` in the UI. Verified live: add → list → update → remove → list-empty, plus role gating (Developer blocked 403 on writes, still able to read).

### Epic 1.2 — Requirement Intake (REAP-030..033) — ✅ done (2026-09-17)
- [x] Manual/rich-text requirement entry — **intentional deviation, not an oversight (decided 2026-09-22):** plain `<textarea>`, no WYSIWYG formatting. The raw text is sent to the LLM as a flat string either way, so bold/lists/links would be stored and displayed but never actually consumed by the pipeline — a real editor dependency (TipTap/Slate/etc.) plus new sanitization/storage-format concerns bought no behavioral capability for this project's scope. Revisit only if a future feature actually reads structured formatting from the raw source.
- [x] Paste meeting notes
- [x] Upload TXT — endpoint + UI (file input in `RequirementWorkspace`) verified live end-to-end. PDF/DOCX remain Phase 2 (ADR-001 §4).
- [x] `RequirementSource` persisted immutably; never overwritten by AI output — verified live

### Epic 1.3 — AI Analysis & Clarification (REAP-040..042) — ✅ done (2026-09-17)
- [x] `RequirementAnalysisService`: raw text → actors/capabilities/data/rules/missing-info (structured JSON, schema-validated via `AiJsonParser` per REAP-005) — verified live against real SQL Server
- [x] `ClarificationService` + `IArtifactService.AnswerClarificationAsync`: clarification questions generated from "missing info"; Open→Answered→Resolved (on reviewer approval)/NotApplicable lifecycle verified live (REAP-042; "Resolved" wired 2026-09-22 — see `ArtifactService.ResolveClarificationIfAnsweredAsync`)
- [x] Clarification Engine folded into `RequirementAnalysisService` rather than built as a separate ambiguity/contradiction-detection pass — **intentional deviation, not an oversight (decided 2026-09-22):** the current design already satisfies §8's actual intent (missing/ambiguous information surfaces as a clarification question instead of a fabricated detail, per §32); a standalone pass would either re-run the same analysis prompt a second time for no new capability, or need a materially different prompt whose value is speculative. `ClarificationService` remains the read/answer side only (see its doc comment).
- [x] UI: `RequirementWorkspace`/`ClarificationQuestionCard` — user answers questions inline, answers persist and version

### Epic 1.4 — Structured Requirement Generation (REAP-043..047) — ✅ done (2026-09-17)
- [x] Functional Requirement Generator + editable UI (`ArtifactCard`) + unique IDs (`FR-001`, …) — verified live
- [x] Non-Functional Requirement Generator, with `proposed_assumption` vs `confirmed` flag **force-set server-side** (never trusted from the model) on any target value (REAP-045, REAP-011) — verified live
- [x] User Story Generator, linked to functional requirements via `ArtifactRelationship(DerivedFrom)` — verified live
- [x] Acceptance Criteria Generator (Given/When/Then; positive/negative/boundary), per story, linked via `DerivedFrom` — verified live

### Epic 1.5 — Human-in-the-Loop Baseline (REAP-074, partial) — mostly done
- [x] Artifact status machine: AI Generated → Approved/Rejected wired end-to-end (`PATCH /api/artifacts/{id}/status`, gated to Administrator/BusinessAnalyst/Reviewer, writes an `ArtifactReview` row) — verified live. Draft/UnderReview/Implemented/Verified states exist in the enum but nothing transitions into them yet.
- [x] Visible "AI Generated" / "AI Generated — Human Review Required" badge in the UI

### Epic 1.6 — Basic Dashboard (REAP-083, partial) — ✅ done (2026-09-17)
- [x] Requirement counts, FR/NFR split, unresolved-question count, approved/pending/rejected split, recent changes — `GET /api/projects/{id}/dashboard`, `DashboardPanel` in the UI. Verified live: counts move correctly as artifacts are generated/answered.

## Phase 1 — ✅ Complete (2026-09-17)

**Exit criteria (Phase 1 demo) — ✅ verified live against a real SQL Server:** created a project, pasted the complaint-management prompt, ran AI analysis (actors/capabilities/missing-info + 2 clarification questions), answered one question (version bumped, Origin=Human on that version), generated FR-001/NFR-001 (NFR correctly forced to `proposed_assumption`), generated US-001 (linked DerivedFrom FR-001), generated AC-001/AC-002 (linked DerivedFrom US-001), approved FR-001 (ArtifactReview recorded), uploaded a .txt requirement source, and watched the dashboard counts update accordingly. Confirmed role gating (Developer blocked with 403 on every write endpoint) and CORS working from the Vite dev origin. **Known gap:** no actual visual browser check of the UI was possible (no browser-automation tool available in this environment) — verification is via the API contract directly plus the React type-check/build succeeding; the UI has not been eyeballed running in a real browser.

---

## Phase 2 — AI SDLC (PDF §34) — ✅ Complete (2026-09-17)

Goal: extend from "requirements" to "design + plan + tests," add quality/conflict guardrails.

All of §11 (business rules), §14 (quality analysis), §15 (conflict detection), §16-18 (solution/DB/API design), §19-20 (tasks/test cases), and Epic 2.4 (PDF/DOCX parsing) are built and verified live end-to-end, following the same generalized-artifact pattern from Phase 1 — no new architectural concept was needed for any of it, which is exactly what ADR-002 was betting on. Phase 2 is fully complete.

### Epic 2.1 — Business Rules & Quality (REAP-050..052) — ✅ done (2026-09-17)
- [x] Business Rule Extraction, linked to requirements — `BusinessRuleService`, `POST /api/requirement-sources/{id}/generate-business-rules`, UI section in `RequirementWorkspace`. Verified live: BR-001 correctly linked to FR-001 via `LinkedRule`.
- [x] Requirement Quality Analysis with concrete recommendations — `RequirementQualityService`, `POST /api/requirement-sources/{id}/analyze-quality`. Ephemeral (not persisted as an artifact — there's no "finding" artifact type), logged via `AIExecution`. Verified live.
- [x] Duplicate/Conflict Detection → "Human Resolution Required" outcome — `ConflictDetectionService`, project-wide scan, `POST /api/projects/{id}/detect-conflicts`, `ConflictsPanel` in the UI. Findings recorded as `ArtifactRelationship(ConflictsWith|DuplicateOf)`, never auto-resolved. Verified live (empty-result path only — the stub AI client returns no findings by design; a real model is needed to exercise an actual detected pair).

### Epic 2.2 — Design Assistants (REAP-060..062, 064) — ✅ done (2026-09-17)
- [x] Solution Design Assistant (architecture/modules/integration/auth/jobs/caching/logging/deployment notes) — `SolutionDesignService`, `POST /api/requirement-sources/{id}/generate-design`, one `DA-xxx` artifact per call. Verified live.
- [x] Database Design Assistant (entities/fields/types/keys/relationships/indexes) — `DatabaseDesignService`, `POST .../generate-data-entities`, `DE-xxx` artifacts linked `DerivedFrom` the FR(s) they support. Verified live. (Developer-review gate = the standard artifact approval workflow, same as every other type — no separate gate needed.)
- [x] API Design Assistant (contract generation linked to requirement IDs) — `ApiDesignService`, `POST .../generate-api-specs`, `API-xxx` artifacts linked `DerivedFrom` FR. Verified live.

### Epic 2.3 — Planning & QA (REAP-063, 070) — ✅ done (2026-09-17)
- [x] Implementation Task generator (desc/type/priority/deps/related-reqs/role/estimate) — `ImplementationPlanningService`, `POST .../generate-tasks`, `TASK-xxx` artifacts linked `Implements` the FR(s) they realize, aware of already-generated data entities/API specs for context. Verified live.
- [x] Test Case Generator (positive/negative/boundary/permission/validation), linked to requirement IDs — `TestGenerationService`, `POST /api/artifacts/{frId}/generate-test-cases` (scoped per FR, like acceptance criteria are per story), `TC-xxx` artifacts linked `TestedBy` (FR → TestCase). Verified live.

### Epic 2.4 — File Intake Completion (carry-over from Phase 1 if deferred) — ✅ done (2026-09-17)
- [x] PDF/DOCX upload parsing (REAP-032) — `IDocumentTextExtractor`/`DocumentTextExtractor` (PdfPig for PDF, DocumentFormat.OpenXml for DOCX, per ADR-001 §4), wired into both upload endpoints (`requirement-sources/upload` and `documents/upload`, since both accept the same file types). Verified live: generated a real PDF and DOCX with distinct content, uploaded both through each endpoint, confirmed correct extracted text in the response; confirmed an unsupported extension (.xyz) is rejected with 400. **Known limitation, not silently glossed over:** PDF extraction is text-layer only (no OCR) — a scanned/image-only PDF yields empty text and is rejected with a clear error rather than silently creating an empty source; DOCX extraction is paragraph text only (tables/headers/footers/embedded objects are out of scope for this pass).

**Exit criteria (Phase 2 demo) — ✅ verified live against a real SQL Server:** from a requirement source with FR-001 already generated, ran generate-design → DA-001, generate-data-entities → DE-001 (linked `DerivedFrom` FR-001), generate-api-specs → API-001 (linked `DerivedFrom` FR-001), generate-tasks → TASK-001 (linked `Implements` FR-001, aware of DE-001/API-001 as context), and generate-test-cases on FR-001 → TC-001/TC-002 (FR-001 linked `TestedBy` both). Confirmed the full traceability graph by querying `GET /api/artifacts/{FR-001 id}/relationships` and seeing all five links (2 TestedBy outgoing, DerivedFrom×2 and Implements×1 incoming) in one place. Role gating reconfirmed (Developer blocked 403 on `generate-design`, still able to read). All reviewable/editable through the same generic artifact endpoints as every other type — no special-casing needed thanks to the ADR-002 generalized model.

---

## Phase 3 — Advanced Intelligence (PDF §35) — ✅ Complete (2026-09-17)

Goal: close the loop — traceability as a live graph, change-impact awareness, versioning, RAG-grounded Copilot, full audit trail.

### Epic 3.1 — Traceability & Change Management (REAP-071..073) — ✅ done (2026-09-17)
- [x] Traceability Matrix as a queryable graph (Objective→Req→Story→AC→Design→Task→Test) — `TraceabilityService`, `GET /api/projects/{id}/traceability-matrix`, `TraceabilityMatrixPanel`. Verified live: one FR rolled up a story, 2 AC, a data entity, a task, and 2 test cases in a single query.
- [x] Impact Analysis on approved-requirement change (advisory only — REAP-072) — `ImpactAnalysisService` (undirected 3-hop BFS over the relationship graph, split by downstream-approval-status), `GET /api/artifacts/{id}/impact`, surfaced via "Check impact" in `ArtifactDetails`. Verified live; never mutates anything.
- [x] Requirement Versioning + version-history UI, AI-vs-human origin tracked — the version endpoint existed since Phase 1; `ArtifactDetails` now renders it with AI/Human badges, plus a two-version side-by-side field diff (pick any two versions from dropdowns, changed fields highlighted) — entirely client-side, since the existing versions endpoint already returns each version's full data snapshot. Verified live: edited an artifact's precondition text, confirmed the resulting v1→v2 diff showed only that field changed and every other field identical.

### Epic 3.2 — RAG & Copilot (REAP-080..082) — ✅ done (2026-09-17)
- [x] Document extraction → chunking → embeddings → vector store pipeline. Embedding-provider decision (carried from ADR-001 §2) resolved: OpenAI `text-embedding-3-small` via `OpenAiEmbeddingClient`, with a `StubEmbeddingClient` default so the app runs with no external key — see ADR-001 §2. `DocumentService` chunks plain text (1200 chars, 200 overlap) and embeds each chunk into `DocumentChunk.Embedding`; no separate vector store/index — similarity is in-process cosine over a project's chunks (also ADR-001 §2).
- [x] Semantic retrieval wired into LLM calls — `CopilotService` retrieves top-5 chunks above a similarity threshold and includes them as cited excerpts in the prompt.
- [x] Project Copilot Q&A (grounded, cites sources) — `CopilotService`/`CopilotController`/`CopilotPanel`. Answers are grounded in two kinds of context: structured facts (untested FRs, open clarifications, pending-review artifacts, recent changes — same underlying data as Traceability/Impact Analysis) plus semantic search over uploaded documents. Ephemeral like quality/conflict analysis — never persists an artifact, only logs an `AIExecution` (`ProjectCopilotQuery`) for the audit trail. Verified live: document upload → chunk → embed → ask → cited answer → audit trail entry, against a real SQL Server.
- PDF/DOCX extraction for uploaded documents shipped with Epic 2.4 (`IDocumentTextExtractor`, shared with requirement-source upload) — no longer TXT-only.

### Epic 3.3 — Audit & Full Dashboard (REAP-009..010, 083) — mostly done
- [x] AI Audit Trail — `AuditTrailService`, `GET /api/projects/{id}/ai-executions`, `AuditTrailPanel`. Explicitly no chain-of-thought field (REAP-010, unchanged from Phase 0). **PromptTemplateVersion, ProducedArtifactId, and Accepted/Rejected wired 2026-09-22** (previously schema-only): `GenerationSupport.AddExecutions` now logs one `AIExecution` row per produced artifact (all sharing the same underlying `OutputJson`, since they represent one AI call fanned out per resulting artifact) rather than one row for the whole call — this is what makes a single-column `ProducedArtifactId` FK correctly model even a call that produces several artifacts, with no join table needed. Each service's `SystemPrompt` has a sibling `PromptTemplateVersion` constant (e.g. `"RequirementGeneration-v1"`), bumped by whoever next edits that prompt. `ArtifactService.UpdateStatusAsync` sets `Accepted` on the artifact's originating execution(s) when a human approves/rejects it. Calls that produce no artifact (quality analysis, conflict detection, copilot queries, the review agent's narrative) still log exactly one execution row with `ProducedArtifactId = null`. See `AuditTrailFieldsTests.cs`.
- [x] Full dashboard: approval progress and recent changes shipped in Phase 1's basic dashboard. **Conflict count and unified test coverage wired 2026-09-22:** `ProjectDashboardResponse.ConflictCount` (`DashboardService`) reports the count of `ArtifactRelationship` rows already on record as `ConflictsWith`/`DuplicateOf` for the project — it reads what `ConflictDetectionService` (`ConflictsPanel`'s "Detect conflicts" action) has recorded, it never re-runs detection itself. Surfaced as a "Potential conflicts" row in the per-project overview's health panel and as a "Conflicts detected" metric card (summed across sampled projects) on the workspace-wide Dashboard. Test coverage was previously calculated two different, inconsistent ways: the workspace Dashboard did a raw ratio of total test-case count to total requirement count (could read as >100% covered if a few requirements hog all the tests while others have none), while the Traceability panel measured the share of functional requirements with at least one linked test case. Both now call the single `testCoveragePercent()` in `web/src/lib/coverage.ts`, which uses the traceability-matrix-based definition (the more meaningful one) everywhere. See `DashboardConflictCountTests.cs`.

### Epic 3.4 — Full Approval Lifecycle (REAP-074, completion) — ✅ done (2026-09-17)
- [x] Implemented/Verified states wired to actual downstream completion signals — `ArtifactService.PromoteLinkedFunctionalRequirementsAsync`, triggered when an `ImplementationTask` or `TestCase` is approved. A Functional Requirement auto-promotes Approved→Implemented once *all* of its linked tasks (`Implements`) are Approved, and Implemented→Verified once *all* of its linked test cases (`TestedBy`) are Approved. This is the only real "completion signal" the platform has (it doesn't execute code or run tests itself), so nothing beyond artifact-review state is used to drive it. Verified live: approving the sole task promoted FR-001 to Implemented; approving one of two test cases left it at Implemented; approving the second promoted it to Verified. **Known limitation, documented not hidden:** promotion is one-directional — there's no demotion if a task/test is later un-approved (would need a real state machine for artifacts, which doesn't exist); only Functional Requirements get this treatment since only they carry `Implements`/`TestedBy` links in this system.

**Exit criteria (Phase 3 demo = PDF §37 in full) — ✅ done (2026-09-22):** Run the complaint-management scenario end-to-end from raw prompt through traceability matrix; then change one approved requirement and show the system correctly identifying (not auto-changing) impacted downstream artifacts. This is REAP-100/REAP-101, captured as an automated integration test rather than a manual walkthrough: `tests/AiReap.Tests/Section37DemoTests.cs`, run against a real SQL Server. It asserts Impact Analysis names the actual linked user story/task/test-case ids after the edit, not just a non-empty or non-mutating response.

---

## Phase 4 — Agentic SDLC (PDF §36, optional / stretch) — ✅ Complete (2026-09-19)

Design and rationale: `docs/architecture/ADR-003-agent-pipeline.md`.

- [x] Each agent's responsibility + structured input/output contract — `IAgent` / `AgentOutput` (summary, metrics, produced artifact refs, findings, review guidance) in `AiReap.Application/Agents`. Six agents: Requirements, Analysis, Architecture, Development Planning, QA, Review. They are thin orchestration over the Phase 1–3 services (no duplicated prompts); only the Review Agent adds a prompt (narrative on top of deterministically computed gaps). `GET /api/agents` exposes the definitions.
- [x] Chained with explicit human-approval checkpoints (REAP-091) — `AgentOrchestrator` persists `AgentRun`/`AgentStageRun` (migration `AddAgentRuns`); every stage ends in `AwaitingApproval` (or `Failed`) and only `POST /api/agent-runs/{id}/decision` advances it. Each agent names its own approver roles (Requirements/Analysis → BusinessAnalyst, Architecture/Planning → Developer, QA → QA, Review → Reviewer; Administrator always). Reject halts the run. Failed stages can be retried or abandoned. UI: `AgentPipelinePanel`.
- [x] Autonomous code deployment out of scope (REAP-092) — structural, not just a policy: there is no deploy/release agent, no code-generation or execution path, and the Review Agent is read-only. Agents plan and document; they don't build or ship anything.

### Phase 4 verification & stabilization (2026-09-19)

Verified by execution, not inspection: `AiReap.Tests` (18 integration tests, real SQL Server + real EF migrations, throwaway DB per test) and a real-browser run (Playwright driving Edge against a throwaway database) covering register/login, project + requirement-source creation, the full pipeline, reject, retry-after-backend-restart, stale-tab conflict, and the five-role approval matrix at every stage.

Bugs found and fixed during verification:
- **Concurrent start/approve/retry raced** — two simultaneous requests both passed the state check, ran the next stage twice, and the loser got an unhandled 500. Fixed with a `rowversion` on `AgentStageRun` (only one caller can claim a decision/retry) and a filtered unique index enforcing one active run per source (migration `AgentConcurrencyGuards`); losers now get 409.
- **(Phase 1–3, pre-existing) Artifact `data` was returned PascalCase while the whole UI reads camelCase** — any project with clarification questions white-screened the app for every role, and other artifact cards showed empty fields. Fixed in one place (`ArtifactJson`, used by `ArtifactResponseMapper` and the versions endpoint); stored data is unchanged. Never caught earlier because the UI had only been type-checked, not run.
- Agent panel didn't notice a requirement source added moments earlier (needed a page reload) — panel now reloads on the shared `refreshKey`.
- Invalid `<ol>` inside `<p>` in the test-case card (React DOM-nesting console error).

**Known limitations, stated plainly:**
- Stages run **synchronously inside the HTTP request**. With a real model the Analysis stage makes many calls and can take minutes; a background worker is the fix if that becomes a problem.
- A stage left `Running` by a process crash mid-request has no recovery path (no heartbeat/timeout).
- A stage approval is a **"proceed" decision, not an approval of the generated artifacts** — those keep their own approval workflow. The Review Agent lists what's still unreviewed.
- "Reject" is terminal for that run; a new run reuses existing artifacts instead of regenerating (no delete/regenerate yet).
- The UI does not auto-refresh across users: a second person sees a stage change only after reloading. A stale approve/reject shows a clear "not awaiting a decision" error rather than acting.
- The Agent Pipeline panel shows raw provider error text on a failed stage (e.g. connection errors); acceptable for now, but review before exposing to end users.
- Retry/failure was verified with a simulated provider outage (unreachable endpoint) and a controllable AI client in tests; it has not been exercised against a real model.
- oxlint reports pre-existing style warnings (e.g. set-state-in-effect); none are build errors.


## Authentication & role management (2026-09-20)

- [x] Public sign-up can no longer choose a role: `POST /api/auth/register` has no role field (a supplied one is ignored) and creates an account with **no role**. A default authorization policy (`Program.cs`) requires one of the five roles on every `[Authorize]` endpoint, so a role-less account can sign in but cannot read or do anything (UI shows "Waiting for a role").
- [x] Administrator user management — `UsersController` (`GET/POST /api/users`, `PUT /api/users/{id}/role`, `PUT /api/users/{id}/status`), class-level `[Authorize(Roles = Administrator)]`; UI at `/admin/users` (link visible to Administrators only). Reuses ASP.NET Identity, `UserManager` and `JwtTokenService` — no new tables and no migration (deactivation = Identity lockout). One role per user; `Reviewer` is the Reviewer/Manager role.
- [x] First Administrator: `AdminBootstrapper` creates it at startup from `Bootstrap:AdminEmail`/`AdminPassword` only when no Administrator exists; no default credential; never touches an existing account; never logs the password. Roles are now seeded in every environment (previously Development only).
- [x] Changes take effect immediately: tokens carry the user's security stamp and `TokenValidation` re-checks user + stamp + lockout on every request; role changes and (de)activation rotate the stamp. The web client signs the user out on a 401 for a token it sent.
- [x] Guards: an administrator cannot change or deactivate their own account, which guarantees at least one active Administrator always remains.

Verified by `AuthAndUserManagementTests` (14 tests) and a real-browser run (sign-up page, waiting screen, all five roles created through the UI, role change, deactivation, non-admin blocked at UI and API).

**Known limitations:** no password reset or self-service password change; no user deletion (deactivate instead); no email verification or invite flow (an admin sets a temporary password out of band); users have one role; existing installs need every user to sign in once after upgrading (old tokens lack the security stamp); `Bootstrap:AdminPassword` stays in configuration until you remove it (it is ignored once an admin exists).

## Project-level access control (2026-09-22)

- [x] `ProjectMember` table (migration `AddProjectMembers`, with a same-migration backfill: every existing user into every existing project, so introducing membership doesn't lock anyone out of data they already had). Deliberately carries no "role on this project" - the app has one role vocabulary (the account's global role), not two; see the entity's doc comment.
- [x] `IProjectAccessService`/`ProjectAccessService` — membership check, with Administrator bypassing it entirely (consistent with Administrator already being included in every other role gate).
- [x] Enforced on **every** project-scoped endpoint, not just `ProjectsController` — two layers: `ProjectMembershipFilter`, a global action filter keyed on a route literally named `{projectId}`, covers the ~10 direct routes (Dashboard, Audit, Copilot, Documents, RequirementSources list/create, Traceability matrix, Agents list, Projects itself and its Stakeholders sub-resource) with no per-action code; the ~15 indirect routes (an artifact/source/run id, where the project is only known once that entity is loaded) call `EnsureMemberAsync` directly inside the relevant Application service - `ArtifactService`, `RequirementSourceService`, `ClarificationService`, every AI generation service (`RequirementAnalysisService`, `RequirementGenerationService`, `BusinessRuleService`, `UserStoryService`, `RequirementQualityService`, `SolutionDesignService`, `DatabaseDesignService`, `ApiDesignService`, `ImplementationPlanningService`, `TestGenerationService`), `ImpactAnalysisService`, and `AgentOrchestrator`.
- [x] `ProjectAccessDeniedException` → 403 via a dedicated `IExceptionHandler` (`ProjectAccessDeniedExceptionHandler`, registered ahead of the general `GlobalExceptionHandler` from the exception-handling pass) rather than a try/catch in every action.
- [x] Creator is auto-added as a member on project creation (`ProjectService.CreateAsync`). Membership management: `GET/POST /api/projects/{id}/members`, `DELETE /api/projects/{id}/members/{userId}`, BA/Admin only (mirrors stakeholder-management gating) - added by email, not a raw user id, since BusinessAnalyst doesn't have access to the Administrator-only `/api/users` list to look one up.
- [x] `GetAllAsync` (`GET /api/projects`) filters the list to the caller's own memberships (Administrator still sees everything), not just individual-project reads.

**Known limitations:** a random/nonexistent project id and a real one you're not a member of are both reported as 403, not 404 - not distinguishing them is deliberate (same non-leaking pattern as other authorization failures in this app), but it is a deviation from strict REST "404 for doesn't-exist" semantics; re-adding an existing member returns 201 instead of 200 (harmless, just an imprecise status code). Verified by `ProjectAccessControlTests` (3 tests: creator auto-membership, a non-member refused on both a direct and an indirect route plus Administrator's bypass, and the member add/remove/role-gating lifecycle) plus updated setup across `RegressionTests`, `Section37DemoTests`, `ClarificationResolutionTests`, `AuditTrailFieldsTests`, and `AgentPipelineTests` (each now explicitly adds the non-creator roles it exercises as members, rather than relying on the old unrestricted-read behavior).

## Polish pass (2026-09-22)

- [x] **Logging.** Before this pass only 2 files in the whole codebase logged anything (`UsersController`, `GlobalExceptionHandler`). Every Ai/Pipeline service (plus `CopilotService` and `ReviewAgent`, which make the same kind of call outside that folder) now logs the two places a production failure actually needs debugging context: the LLM call itself (start/success/failure, with operation type, model, and an input reference - a source/story/project id, never the full prompt) and JSON-validation failures on the response. Centralized once in `GenerationSupport.CallAiAndParseAsync<T>` rather than duplicated per service, since every service already called `_chatClient.CompleteAsync` immediately followed by `AiJsonParser.Parse<T>`. `ReviewAgent` is the one exception: it deliberately swallows a malformed narrative and falls back to a deterministic summary (not a hard failure), so it logs inline instead of using the throwing helper, turning what used to be a silent `catch` into a logged `LogWarning`.
- [x] **RAG scaling note.** `CopilotService.RetrieveChunksAsync` loads every chunk for a project and scores it in memory - fine at demo scale, not indexed. Left a `TODO` there (no implementation) pointing at an indexed vector store as the fix if a project's corpus grows materially.
- [x] **Dynamic role matrix.** `web/src/pages/RolesPage.tsx` used to hand-mirror the API's `[Authorize(Roles = ...)]` rules as a static table - and had already drifted once (the §38 project-membership endpoints were never added to it). Replaced with `GET /api/roles/matrix` (`RolesController`), which reflects over the actually-loaded controller actions at request time. A lightweight `[Capability(group, label, order)]` attribute marks the one action that best represents each user-facing capability (so the page keeps readable grouped rows instead of ~40 raw routes); the *roles* shown for that row are always read live from that action's real `[Authorize]` (or its controller's, or - for a bare `[Authorize]` with no Roles - every defined role). Where one label already spans several endpoints that share a role set today (e.g. all four requirement-source generation actions), only the representative action is tagged; if those diverge later, only the tagged one is reflected - a disclosed simplification, not a bug. Verified by `RoleMatrixTests`, which specifically asserts the previously-missing "Add or remove project members" row is now present with the correct roles.

## Release readiness pass (2026-09-23)

Follow-up to an independent evaluation of the whole app against the PDF (build it, run it, run the real test suite — not just re-read this file). Closed every gap that survived that check:

- [x] **Fixed the 2 failing `AuthAndUserManagementTests`** — root cause and fix described under the Release Gate section below (`TestHost` was leaking the developer's own local user-secrets into the test host).
- [x] **CI** — `.github/workflows/ci.yml`: a `backend` job (SQL Server service container, `dotnet build` + `dotnet test`) and a `frontend` job (`npm ci`, `npm run lint`, `npm run build`) on every push/PR to `main`. `TestHost.ServerConnection` is now overridable via `AIREAP_TEST_SQL_CONNECTION` so CI's service container doesn't have to share the local dev container's port.
- [x] **Project-member management UI** — `MembersPanel.tsx` (mirrors `StakeholdersPanel.tsx`: list, add by email, remove), wired into the Gathering page's tab strip alongside Stakeholders. `ProjectsController.GetMembers`/`AddMember` now resolve each member's email/display name via `UserManager` (the Application-layer `ProjectMemberResponse` stays `UserId`-only by design — identity lookups live in the controller, same pattern `AddMember` already used) so the panel has something to show. Verified by a new `ProjectAccessControlTests.Member_responses_are_enriched_with_email_and_display_name` test, plus a clean `npm run build`/`npm run lint`.

## Release Gate — Definition of Done (PDF §38, REAP-102) — ✅ Closed (2026-09-23)

Walked item by item against the actual code/test state (not just prior status notes) as part of the post-evaluation cleanup pass. Every item below is backed by a passing automated test against a real SQL Server (`dotnet test AiReap.sln` — 47/47) plus a clean `dotnet build`/`npm run build`.

- [x] React ↔ ASP.NET Core integration working end-to-end — frontend (`npm run build`) type-checks and builds against the real API surface; every integration test drives the API the same way the UI does (`TestHost`/`ApiUser`).
- [x] SQL Server persistence with the justified schema from the Phase 0 ADR — `ADR-002-data-model.md`; every test runs real EF migrations against SQL Server (`TestHost.cs`), not an in-memory provider.
- [x] Auth + role-based authorization across multiple projects — `AuthAndUserManagementTests` (14 tests) + `ProjectAccessControlTests` (4 tests), all passing.
- [x] Full AI requirement analysis + clarification workflow — `ClarificationResolutionTests`, `Section37DemoTests`.
- [x] FR/NFR generation, business rules, stories, acceptance criteria — `Section37DemoTests`, `RegressionTests`.
- [x] Requirement quality + conflict analysis — `DashboardConflictCountTests`.
- [x] Design, implementation-task, and test-case assistance — `Section37DemoTests` (full chain: design → data entities → API specs → tasks → test cases).
- [x] Traceability, versioning, and change-impact analysis — `Section37DemoTests` asserts impact analysis names the actual linked downstream artifacts after an edit, not just a non-empty response.
- [x] Human approval workflow + AI Project Copilot — `ArtifactEditTests`; Copilot verified live per Epic 3.2 (no automated test — grounded LLM Q&A isn't meaningfully assertable against the stub client).
- [x] AI execution/audit history (no hidden chain-of-thought) — `AuditTrailFieldsTests`.
- [x] Clear AI-vs-human-decision distinction visible throughout the UI — `ArtifactCard`/`ArtifactDetails` render the "AI Generated — Human Review Required" badge and origin markers on every artifact type; carries the same standing caveat as the rest of this document (no real-browser visual walkthrough has been done in this environment — verified via the API contract and a type-checked/built React app, not eyeballed running).
- [x] Exception handling, logging, clean source control, setup/architecture docs — `GlobalExceptionHandlerTests`, `ProjectAccessDeniedExceptionHandler`; logging pass (Polish pass, 2026-09-22); git history + `docs/` (ADRs, requirements analysis, this roadmap); CI now enforces build+test on every push/PR (`.github/workflows/ci.yml`).

**Fixed during this pass, not just verified:** two integration tests (`AuthAndUserManagementTests.Bootstrap_never_takes_over_an_existing_non_admin_account...` and `...Without_bootstrap_credentials_no_account_is_created...`) were failing — not a product bug, but `TestHost`'s `WebApplicationFactory` was picking up the *developer's own* local `dotnet user-secrets` (`Bootstrap:AdminEmail`/`AdminPassword`, set per this README's own local-dev instructions) since ASP.NET Core auto-loads user secrets in the `Development` environment tests also run under. `TestHost.Build()` now explicitly strips the user-secrets configuration source and force-blanks the bootstrap keys when a test wants "no bootstrap configured," so tests are hermetic regardless of what's in the machine's secrets store — see `tests/AiReap.Tests/TestHost.cs`.

## Working Agreement While Building (PDF §39)

For every AI-assisted change in this repo: **Understand Requirement → Design → Generate/Write Code → Review → Build → Test → Review AI Output → Commit.** No generated diff gets committed unless the developer (you) can explain what it does, why it's needed, how it fits the rest of the system, and how it was tested — this applies to Claude-authored code in this repo just as much as any other AI assistant.
