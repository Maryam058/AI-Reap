# AI-REAP — Requirements Analysis

Source: `AI_Requirements_Engineering_SDLC_Automation_Project.pdf` (39 sections, analyzed 2026-09-17)
Purpose: turn the source PDF into an engineering-grade breakdown that the rest of `docs/` (traceability matrix, roadmap) is derived from. This document is the single source of truth for "what the PDF says"; the traceability matrix is "how we tracked it"; the roadmap is "when we build it."

---

## 1. Vision & Product Shape

AI-REAP is **not a chatbot**. It is a system of record for the early SDLC — projects, requirements, versions, relationships, approvals, AI-generated artifacts, and traceability — with an AI layer that assists a human-driven workflow. The AI plays five personas across the lifecycle:

- AI Business Analyst
- Requirements Engineer
- Solution Design Assistant
- Project Planning Assistant
- QA Assistant

**Non-negotiable design principle (appears repeatedly — §6, §10, §24, §32):** AI output is always a *proposal* layered on top of retained human/source input. Original input is never overwritten. Numeric targets, assumptions, and generated artifacts are labeled `AI Generated — Human Review Required` until a human approves them.

## 2. Technology Stack (fixed, not negotiable)

| Layer | Choice |
|---|---|
| Frontend | React + TypeScript, responsive, reusable components, REST client |
| Backend | ASP.NET Core Web API, C#, layered/clean architecture, DI, EF Core, Swagger/OpenAPI |
| Database | Microsoft SQL Server — system of record for everything (see §11 data model) |
| AI | Provider-abstracted LLM integration; structured JSON output preferred over free-text parsing; RAG; controlled agentic workflows (Phase 4 only) |

Architecture mandate (§30): `React → ASP.NET Core API → Application Services (one per capability) → AI Orchestrator → {LLM, RAG/Vector Store}`. The AI provider must be swappable without touching business logic — this implies an `ILlmProvider`/`IAiOrchestrator` abstraction from day one, not a refactor later.

## 3. Primary End-to-End Workflow

```
Create Project → Enter/Upload Raw Requirements → AI Requirement Analysis →
Identify Missing Information → AI Clarification Questions → User Answers/Refines →
Functional + Non-Functional Requirements → Business Rules + User Stories + Acceptance Criteria →
Solution/API/Database Design → Implementation Tasks → Test Cases →
Traceability Matrix → Review/Approval
```

This is the backbone the UI and API surface must be built around — it's a pipeline of AI-assisted stages, each producing an editable, versioned, human-approvable artifact that links back to its inputs.

## 4. User Roles & Authorization

| Role | Permissions |
|---|---|
| Administrator | Manage users, AI configuration, system settings |
| Business Analyst | Create projects, enter requirements, run AI analysis, edit/approve artifacts |
| Developer | View approved requirements, designs, stories, implementation tasks (read-mostly) |
| QA | View requirements, generate/manage test cases |
| Reviewer / Manager | Review, comment, approve, or reject artifacts |

Role-based authorization is a **backend-enforced** requirement, not just UI hiding — every API endpoint needs an authorization policy.

## 5. Project Lifecycle

Each project is an isolated AI context (own requirements, own RAG index, own history). Status machine:

```
Draft → Requirements Gathering → Analysis → Design → Approved → Implementation → Testing → Completed
```

Project entity fields: name, description, business problem, objectives, scope, domain, target users, stakeholders, technology preferences, constraints, expected timeline, status.

## 6. AI Requirement Intake (§6)

Input channels: manual text entry, rich-text description, pasted meeting notes, uploaded TXT/PDF/DOCX. **Hard rule: original source is retained verbatim forever; AI output is always a derived, separate artifact.** This implies a `RequirementSource` entity distinct from any AI-derived `Requirement` records.

## 7. AI Requirement Analyzer (§7)

Transforms vague prose into structured concepts: **Actors, Capabilities, Data, Rules, Missing Information**. Given the sample input ("employee leave system..."), it must decompose into actor lists, capability lists, and an explicit "Missing" checklist — this "Missing" output is the direct input to the Clarification Engine (§8).

## 8. AI Clarification Engine (§8)

Detects: ambiguity, missing information, contradictions, undefined terminology, missing actors/rules/error-scenarios/permissions/data-requirements/integrations/security requirements. Each question has a lifecycle: `Open → Answered → Resolved → Not Applicable`. Questions and answers are first-class stored entities (`ClarificationQuestion` / `ClarificationAnswer`), not ephemeral chat turns.

## 9–13. Structured Artifact Generators

All of these produce **structured, editable, ID-tagged, versioned** records — never raw prose blobs:

- **Functional Requirements (§9):** ID, actor, priority, preconditions, inputs, processing, expected result, dependencies.
- **Non-Functional Requirements (§10):** categories = Performance, Security, Availability, Scalability, Usability/Accessibility, Maintainability, Compliance, Observability. AI-proposed numeric targets are flagged `proposed assumption` until a human confirms — this is a concrete, testable rule (see §32 good/bad example: never assert "10,000 concurrent users" as fact).
- **Business Rules (§11):** ID + statement, must be explicitly linked to the requirement(s) they constrain.
- **User Stories (§12):** persona/value/priority + links to dependencies, related requirements, status.
- **Acceptance Criteria (§13):** Given/When/Then, covering positive, negative, and boundary cases.

## 14–15. Quality & Conflict Analysis

- **Requirement Quality Analysis (§14):** flags ambiguity, incompleteness, inconsistency, non-testability, duplication, contradiction, missing dependency, undefined terms, excessive complexity, unclear actor/outcome — with a concrete recommendation (e.g. "loads quickly" → demand a measurable target).
- **Duplicate/Conflict Detection (§15):** flags near-duplicates, contradictions, overlaps, dependency conflicts. **AI never auto-resolves a conflict — result is always "Potential Conflict Detected — Human Resolution Required."**

## 16–19. Design & Planning Assistants

- **Solution Design (§16):** architecture overview, modules/services, APIs, DB entities, integrations, auth, background jobs, caching, logging, deployment notes — all editable/reviewable, never auto-applied.
- **Database Design (§17):** entities, fields, types, keys, relationships, required/optional, indexes — requires developer review before use.
- **API Design (§18):** contract per endpoint — purpose, method, route, request/response shape, validation, authZ, linked requirement IDs.
- **Implementation Planning (§19):** tasks with description, type, priority, dependencies, related requirements, suggested role, status, optional estimate.

## 20. Test Case Generator (§20)

Positive, negative, boundary, permission/security, and validation tests, each linked to a requirement ID, with preconditions/steps/expected result.

## 21. Traceability Matrix (§21)

Full chain: `Business Objective → Requirement → User Story → Acceptance Criteria → Design → Task → Test Case`. This must be a **queryable relational structure** (join table(s) over a generic `RequirementRelationship`), not a generated report — the Copilot (§25) and Impact Analysis (§22) both depend on querying it live.

## 22. AI Impact Analysis (§22)

When an *approved* requirement changes, the system must identify potentially affected stories/AC/APIs/entities/tasks/tests by walking the traceability graph. **Hard rule: never silently modify approved downstream artifacts** — impact analysis is advisory only; humans decide what changes.

## 23. Requirement Versioning (§23)

Every requirement keeps version history: previous content, new content, changed-by, date, reason, and an `origin` flag (`AI` vs `Human`). Users must be able to diff/compare versions side by side.

## 24. Human-in-the-Loop Approval (§24)

Universal artifact status machine: `AI Generated → Draft → Under Review → Approved → Implemented → Verified`. Any AI-origin artifact must visibly carry an "AI Generated — Human Review Required" badge until it moves past Draft.

## 25. AI Project Copilot (§25)

A project-scoped, RAG-grounded Q&A assistant answering questions like "what's incomplete," "what's unresolved," "what lacks test cases," "what changed," "what does FR-018 changing affect." This is essentially a read-only query layer over the same structured data (§21, §22) plus semantic search (§26) — not a separate knowledge source.

## 26. Project Knowledge / RAG (§26)

Pipeline: `Documents/Specs/Notes → Extraction → Chunking → Embeddings → Vector Store → Semantic Retrieval → LLM`. Generated answers should cite source material where applicable. **Open question:** SQL Server native vector support (2025) vs. external vector store — see Open Questions below.

## 27. AI Audit Trail (§27)

Every AI operation logs: operation type, project, user, timestamp, model, prompt-template version, input reference, output, accepted/rejected, human edits, final version. **Explicit prohibition: never store or expose hidden provider chain-of-thought** — only structured inputs/outputs and decisions.

## 28. Dashboard (§28)

Project-level metrics: requirement counts, FR/NFR split, story counts, unresolved questions, conflicts, approval progress, test coverage, requirements needing review, recent changes.

## 29. Core Data Model (§29) — explicit design challenge

The PDF explicitly warns: **"Do not blindly create one table for every concept. Identify opportunities for a generalized artifact model and justify the final schema."** This is a direct instruction to design a polymorphic/generalized artifact backbone (e.g. a shared `Artifact` base with `ArtifactType`, versioning, relationships, approvals, AI-execution linkage applied uniformly to Requirements, Stories, AC, Design docs, Tasks, Test Cases) rather than 15+ near-duplicate tables each re-implementing status/version/approval/audit. This decision belongs in `docs/architecture/` before EF Core models are written — see Roadmap Phase 1, Epic "Data Model Foundation."

Entities named explicitly: User/Role, Project/ProjectStakeholder, RequirementSource/Requirement, FunctionalRequirement/NonFunctionalRequirement/BusinessRule, ClarificationQuestion/ClarificationAnswer, UserStory/AcceptanceCriterion, DesignArtifact/ApiSpecification/DataEntity, ImplementationTask/TestCase, RequirementRelationship/RequirementVersion, AIExecution/AIArtifact, Document/DocumentChunk, Review/Approval/AuditLog.

## 30. AI Architecture (§30)

Named application services (one per capability, matching the workflow in §3):
`RequirementAnalysisService, ClarificationService, RequirementGenerationService, UserStoryService, DesignGenerationService, ImpactAnalysisService, TestGenerationService, TraceabilityService` — sitting behind an `AI Orchestrator` that wraps the LLM and RAG/vector store. Provider swap must not require business-logic changes → interface-driven design (e.g. `ILlmClient`, `IEmbeddingClient`, `IVectorStore`).

## 31. Structured AI Output (§31)

JSON output is mandatory wherever practical; **all AI responses must be schema-validated before persistence** (e.g. JSON Schema / strict deserialization with rejection-and-retry on malformed output, not best-effort parsing).

## 32. AI Safety & Reliability (§32)

Core rule, restated with a concrete good/bad pair: never assert an unconfirmed fact as a confirmed requirement. Missing info → clarification question or explicit labeled assumption, never a fabricated default. This rule must be enforced at the prompt/schema level (e.g. every generated NFR target field carries a mandatory `status: "confirmed" | "proposed_assumption"`), not just as a style guideline.

## 33–36. Phasing (source of truth for Roadmap)

- **Phase 1 — MVP** (§33): Auth, Project Management, Raw Requirement Entry, AI Requirement Analysis, Clarification Questions, Functional Requirements, Non-Functional Requirements, User Stories, Acceptance Criteria. **PDF explicitly calls sections 1–13 the mandatory first-month deliverable.**
- **Phase 2 — AI SDLC** (§34): Business Rules, Requirement Quality Analysis, Conflict Detection, Solution Design, Database Design, API Design, Implementation Tasks, Test Case Generation.
- **Phase 3 — Advanced Intelligence** (§35): RAG, Document Knowledge Base, Requirement Traceability, Impact Analysis, Version Comparison, Project Copilot, AI Audit Trail.
- **Phase 4 — Agentic SDLC (optional)** (§36): `Requirements Agent → Analysis Agent → Architecture Agent → Development Planning Agent → QA Agent → Review Agent`, each with defined responsibilities, structured I/O, and human approval boundaries. **Explicitly out of scope: autonomous code deployment.**

## 37. Required End-to-End Demonstration (§37)

A fixed acceptance scenario the finished system must be able to run live, start to finish, using a deliberately incomplete prompt:

> "We need a complaint management application where employees can submit complaints and managers investigate them."

Full chain: Raw Requirement → Analysis → Missing Information → Clarification Questions → Updated Requirement → FR/NFR → Business Rules → User Stories → Acceptance Criteria → Architecture → DB Proposal → API Proposal → Implementation Tasks → Test Cases → Traceability Matrix — **then** an important requirement is changed and the system must show identified downstream impact. This scenario should become an actual seed/demo script and, ideally, an automated integration test fixture.

## 38. Definition of Done (§38)

Treat this as the release checklist (mirrored in the Roadmap as a gate, not duplicated in full here):
React↔API integration; SQL Server persistence with justified schema; auth/authz across multiple projects; AI analysis+clarification workflow; FR/NFR generation; business rules/stories/AC; quality+conflict analysis; design/task/test assistance; traceability+versioning+impact analysis; human approval workflow + Copilot; AI audit history; clear AI-vs-human distinction in the UI; exception handling, logging, clean source control, setup/architecture docs.

## 39. Development Rule (§39)

Applies to *how this project itself is built*, independent of the product: no AI-generated implementation code is accepted unless the developer can explain what it does, why it's needed, how it interacts with the rest of the system, and how it was tested. Process: `Understand Requirement → Design → Generate/Write Code → Review → Build → Test → Review AI Output → Commit`. This governs how we (Claude + user) should work together on this repo — treat every generated diff as needing a human "why/how/tested" answer before commit, not just a passing build.

---

## Open Questions / Assumptions (in the spirit of §8's own Clarification Engine)

The PDF is a requirements spec, not an implementation spec — several concrete engineering decisions are left open. Flagging them now rather than silently assuming, per §32's own rule:

1. **LLM provider(s)** — not named. Needs an `ILlmClient` abstraction regardless (§30 mandates this), but which provider(s) to integrate first (Anthropic/OpenAI/Azure OpenAI/local) is undecided.
2. **Vector store for RAG (§26)** — SQL Server 2025 native `VECTOR` type vs. an external store (Azure AI Search, Qdrant, pgvector-on-Postgres-sidecar). Given the "everything in SQL Server" framing, defaulting to SQL Server-native vector search first is likely intended, but not stated.
3. **Auth mechanism** — ASP.NET Identity + JWT vs. an external IdP (Azure AD/Entra, Auth0). Not specified.
4. **File parsing for TXT/PDF/DOCX upload (§6)** — library choice (e.g. iText/PdfPig for PDF, OpenXML SDK for DOCX) unspecified.
5. **Hosting/deployment target** — not mentioned; §16/§30 mention "deployment considerations" only as an AI-suggested output field, not as infra for this project itself.
6. **Multi-tenancy scope** — "multiple projects" (§38) is required; whether multiple organizations/tenants share one deployment is unspecified — assumed single-tenant, multi-project for now.
7. **Data model generalization (§29)** — explicitly flagged by the PDF as a design decision we must make and justify, not given to us. See Roadmap Phase 1 architecture-decision task.

These should be resolved (or explicitly deferred with a stated assumption) before Phase 1 implementation begins, consistent with the product's own philosophy of never silently guessing.
