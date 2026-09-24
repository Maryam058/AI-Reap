# AI-REAP — Project Status

Updated 2026-09-24 after the implementation pass that followed the morning audit (branch `main`, base `9e448e9`, **uncommitted working tree**). Source of truth: the spec PDF (*AI Requirements Engineering & SDLC Automation Platform*) compared against the code. Nothing here is taken from earlier roadmap claims.

**"Complete" means:** DB + service + endpoint + backend authorization + UI (+ for AI: prompt, structured output, validation, persistence) all present and exercised by a test. Anything short of that is **Partial**.

## Verification (fresh, not from stale binaries)

| Check | Result |
|---|---|
| Environment | Stale dev API (PID 89340, started 15:50, holding `src/AiReap.Api/bin`) stopped; the testhost PID 78556 from the audit no longer existed. C: had 0 GB free → 0.83 GB after clearing regenerable caches (npm `_cacache`, NuGet `v3-cache`, temp files > 1 day old). Builds/tests ran with TEMP/NuGet/npm caches redirected to `D:\devcache` (session only, no system-wide change). Node `D:\tools\nodejs` put on PATH for the session only. |
| `bin`/`obj` deleted → `dotnet restore` + `dotnet build AiReap.sln` | **0 warnings, 0 errors** |
| `dotnet test AiReap.sln` on those fresh binaries | **184 passed / 0 failed / 0 skipped** (78 original + 106 new; existing tests in 3 files were updated where the required behaviour changed: Section37DemoTests, AiChatClientSelectionTests, AuditTrailFieldsTests) |
| Baseline before any change (fresh build of `9e448e9`) | 78 passed / 0 failed / 0 skipped |
| `npm run lint` | exit 0, 25 warnings (all pre-existing `set-state-in-effect` / `only-export-components`; none in new code) |
| `npm run build` (`tsc -b && vite build`) | pass |
| `dotnet ef migrations has-pending-model-changes` | none; 2 new additive migrations applied to the dev DB `AIReapDb`; API starts against it |
| Real Gemini | **Not yet exercised** — no Gemini key exists on this machine. Every automated test uses the stub by design. Run `scripts/verify-gemini.ps1` after setting the key (see *Manual actions*). |

## What changed in this pass

| Area | Change |
|---|---|
| Approval integrity (§22/§24) | Explicit state machine (`Domain/Common/ArtifactStatusTransitions`), illegal moves → 409. Editing Approved/Implemented/Verified content creates a new version in **UnderReview**; the approved version is preserved (full snapshot incl. title/priority/status) and `Artifact.ApprovedVersion` points at it until re-approval. Reviews record the version they decided on (`GET /api/artifacts/{id}/reviews`). Implemented/Verified are system-derived only. |
| Impact (§22) | Directed downstream walk (no longer undirected — upstream objectives are never "impacted"), path explanation per hop, and **persistent impact notices** raised automatically when approved content changes; downstream artifacts are never modified; notices are acknowledged by a person (`/api/impact-notices/{id}/acknowledge`). |
| Traceability (§21) | `BusinessObjective` artifact type (`BO-xxx`) seeded from `Project.Objectives`; FRs link to objectives (AI-proposed with validated codes, or manually via `PUT /api/artifacts/{id}/business-objectives`); matrix has a Business Objective column; `GET /api/projects/{id}/business-objectives` shows objective→requirement coverage. |
| Copilot (§25) | Now sees requirement **content** (relevance-ranked, size-capped), a deterministic impact walk for any artifact code in the question, version history ("what changed"), incomplete FRs and open impact notices. Project-scoped only; cited codes validated. |
| RAG (§26) | `GeminiEmbeddingClient` (`gemini-embedding-001`, 768-d) replaces the hash stub when a Gemini key is set; shared `KnowledgeRetriever` feeds both Copilot and requirement generation; FRs store validated `sourceReferences` to document excerpts; `POST /api/projects/{id}/documents/reindex` re-embeds stale chunks. |
| LLM | **Gemini** is the provider (`GeminiAiChatClient`, JSON mode via `responseMimeType`, default `gemini-3.6-flash`), behind the existing `IAiChatClient`. Anthropic/Ollama implementations remain in code but are not configured or required. |
| AI reliability | `ResilientAiChatClient` (per-attempt timeout, exponential backoff + jitter, honours `Retry-After`, gives up fast on long quota waits); `AiProviderException` classification; semantic validation of every AI response (required fields, enum vocabularies, 300-char titles, references to real artifact codes / document excerpts) with **one repair attempt**; nothing persisted on failure. |
| Error handling | `ApplicationExceptionHandler`: AI failures → 429/502/503/504/422 ProblemDetails with `code` + `retryable`; invalid transition → 409; validation → 400. Frontend shows the explanation instead of raw JSON / "unexpected error". |
| Security | No secrets in the repo: connection string, JWT key and Gemini key come from user-secrets / env vars; startup refuses a missing connection string / JWT key and (outside Development) a weak or placeholder JWT key; CI SQL password derived per run; test host reads the SQL server from env or user-secrets; `.vscode` password removed; logs/pid/`.vite` cache untracked and ignored. |
| Roles (§4) | QA can generate test cases and edit **test-case** artifacts (only). |

## `/api/requirement-sources/{id}/analyze` 500 — root cause

From the dev server log (`src/AiReap.Api/backend.dev.log`, now untracked): the call reached Anthropic, which answered **HTTP 400 "Your credit balance is too low to access the Anthropic API"**. `AnthropicAiChatClient` threw `HttpRequestException`; nothing classified it, so `GlobalExceptionHandler` returned a generic 500 "An unexpected error occurred." The pipeline, DB and validation were not at fault. Fix: provider switched to Gemini; provider errors are classified (`ResilientAiChatClient`/`GeminiAiChatClient`) and mapped (`ApplicationExceptionHandler`) — the same billing error now returns **502 `ai_request_rejected`** with the provider's reason (covered by `AiErrorHandlingTests.The_original_billing_rejection_now_reports_the_provider_reason`). Expected success response: see `docs/GEMINI_SETUP.md` §4.

## Spec coverage (verified)

**C** Complete · **P** Partial · **M** Missing · **D** Intentional deviation

| § | Feature | St | Notes / what's missing |
|---|---|---|---|
| 2 | Stack (React/TS, ASP.NET Core, EF, Swagger, DI, layers) | C | |
| 2 | DB stores risks / assumptions / constraints / objectives | P | Objectives are now first-class (`BusinessObjective`). Risks, assumptions, constraints are still project text fields. |
| 4 | Roles enforced on backend | P | QA fixed. **D:** Developer can read unapproved artifacts — required because Developers approve the Architecture/Development-Planning agent stages and review DB design (§17); the spec's "view approved" would break that. Developer cannot set artifact status (review roles only). Admin "AI configuration" is read-only (`/api/ai/status`); settings page still "planned". |
| 5 | Project fields + lifecycle | C | |
| 6 | Intake: manual, paste, TXT/PDF/DOCX; original retained | P | **D:** rich text not built (plain text accepted as the documented deviation). Uploaded file bytes are not retained, only extracted text. |
| 7 | Requirement analyzer | P | Actors/capabilities/data/notes returned and stored in the audit row only, not as artifacts; re-analysis duplicates questions. |
| 8 | Clarification engine | P | Missing-information questions only (no contradiction/undefined-term categories). |
| 9 | Functional requirements | C | Priority/actor/title now validated. |
| 10 | NFRs (8 categories, assumptions) | P | Category now validated against the 8; no "confirm assumption" action; coverage of all 8 not enforced. |
| 11 | Business rules | C | |
| 12 | User stories | P | No dependencies field. |
| 13 | Acceptance criteria | C | Kind validated. |
| 14 | Quality analysis | P | Findings not persisted. |
| 15 | Conflict detection + human resolution | P | Reason not persisted; no resolve/dismiss workflow. |
| 16 | Solution design | C | |
| 17 | Database design (developer review) | P | Developer reviews via the agent stage, not via artifact approval. |
| 18 | API design | C | |
| 19 | Implementation tasks | C | Task board (assignee/status) not built. |
| 20 | Test cases | P | QA can now generate/edit. Per-FR only; no NFR-based tests. |
| 21 | Traceability matrix | C | BO → FR → Story → AC → Design → Task → Test. Limitations: NFRs not rows; design linked via source; no CSV export. |
| 22 | Impact analysis, no silent change | C | Automatic persistent notices + on-demand walk. AI narrative of impact not built. |
| 23 | Versioning + compare | C | Full snapshots and diff incl. title/priority/status. Changed-by shows user id, not display name. |
| 24 | Approval workflow + AI label | C | State machine, re-approval after edit, all transitions in UI. No separation of duties (an editor may approve) — open question. |
| 25 | Project Copilot | C | Grounded in content/impact/history; lexical relevance ranking; no conversation history. Live-model quality **not yet verified**. |
| 26 | RAG pipeline + source refs | C | Real Gemini embeddings when a key is set (hash stub otherwise); SQL `varbinary` vectors + in-memory cosine (fine at project scale); used by Copilot and requirement generation. Live **not yet verified**. |
| 27 | AI audit trail | P | Human edits / final version not linked to executions; 200-row cap. |
| 28 | Dashboard | P | Added open-impact-notice metric; "requirements needing review" list and server-side coverage still missing. |
| 29 | Data model justified | C | ADR-002 still says `ToJson()` (doc drift). |
| 30 | Replaceable provider | C | Gemini behind `IAiChatClient`/`IEmbeddingClient`; no separate "orchestrator" layer (services call the seam). |
| 31 | Structured JSON validated before persistence | C | Provider JSON mode + shape + semantic validation + one repair. |
| 32 | Source vs human vs AI vs assumption | P | Per-artifact origin/status only; assumptions not first-class. |
| 36 | Agent chain (optional) | C | Unchanged: 6 agents, human checkpoint after each, no deployment. Runs inside the HTTP request. |

### Estimates (strict rule above; Partial = ½)

| Phase | Estimate | Basis |
|---|---|---|
| MVP §1–§13 | **~85 %** | Unchanged in substance; validation and QA role improved. Main gaps: analysis persistence, clarification breadth, original-file retention, NFR confirm, story dependencies. |
| Phase 2 §14–§20 | **~75 %** | 3 Complete, 4 Partial (quality/conflict persistence and resolution, developer design review, test breadth). |
| Phase 3 §21–§28 | **~85 %** | 6 Complete, 2 Partial (audit human-edit linkage, dashboard). Copilot/RAG quality against the live model still to be confirmed. |
| Phase 4 §36 | **~90 %** | Unchanged. |

The project is **not complete**: see remaining work.

## Remaining work (ordered)

1. **Live Gemini verification** (manual, needs your key): `scripts/verify-gemini.ps1`; tune prompts if the live model trips validation.
2. **Login throttling/lockout** — not required by the spec, but missing from the security baseline. Identity lockout is currently used to mean "deactivated", so failed-login lockout needs its own flag/message; also rate-limit `/api/auth/*`. JWT is still stored in `localStorage`; self-registration stays open (role-less accounts).
3. Idempotent regeneration (re-running a generator duplicates artifacts) and concurrency-safe artifact codes / optimistic concurrency (`RowVersion`).
4. Persist analysis results (§7), quality findings (§14), conflict reason + resolution workflow (§15).
5. Clarification categories (§8), NFR assumption confirmation (§10), story dependencies (§12), NFR-based tests (§20).
6. Audit trail human-edit / final-version linkage (§27); dashboard needs-review list and server-side coverage (§28).
7. Retain original uploaded files (§6); risks/assumptions/constraints as artifacts (§2).
8. Frontend tests (none exist; not added — no framework in the project); AI calls still run synchronously inside the HTTP request.

## Intentional deviations

* Plain-text intake instead of rich text (§6) — accepted per decision of 2026-09-24.
* Developer can view unapproved artifacts (§4) — needed for developer review of design/plan stages.
* Clarification folded into analysis (no standalone ambiguity pass) — from ROADMAP, unchanged.
* Vector store is SQL Server `varbinary` + in-process cosine similarity — no extra infrastructure.

## Known limitations

* Real-model behaviour (Gemini) unverified until the manual script is run; the free tier's rate limits apply (429 `ai_rate_limited`).
* Documents uploaded before a Gemini key is set are embedded by the hash stub and are invisible to semantic retrieval until `POST /api/projects/{id}/documents/reindex`.
* Existing projects get `BO-xxx` objectives on their next project save or requirement generation.
* Versions written before this pass have no title/priority/status snapshot (the migration backfills each artifact's latest version only).
* Git history still contains the old dev SQL password, the dev JWT placeholder key and the tracked log files (only untracked now; history not rewritten).
