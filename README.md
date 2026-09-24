# AI-REAP

AI Requirements Engineering & SDLC Automation Platform — React + TypeScript frontend, ASP.NET Core Web API backend, SQL Server persistence, LLM/RAG-powered requirements engineering assistant.

Not a chatbot: a system of record for projects, requirements, versions, relationships, approvals, AI-generated artifacts, and traceability, with AI assisting a human-approved SDLC workflow (raw requirement → clarification → structured requirements → design → tasks → tests → traceability).

See [`docs/README.md`](docs/README.md) for the full requirements analysis, architecture decisions, traceability matrix, and phased development roadmap.

**Status:** see [`PROJECT_STATUS.md`](PROJECT_STATUS.md) for the current, verified requirement-by-requirement status (it supersedes the summary below, which predates the 2026-09-24 audit). Historical summary: Phases 0-4 were declared complete on 2026-09-23 — 47/47 integration tests passing against a real SQL Server, CI enforcing build+test on every push/PR. Built and verified: auth/roles (including full administrator-managed user/role management and project-level membership, with a dedicated UI for both), full project management (CRUD, enforced status lifecycle, stakeholders, members), requirement intake (TXT/PDF/DOCX), dashboard, the full AI generation pipeline (analysis → clarification → FR/NFR → business rules → quality/conflict analysis → user stories → acceptance criteria → solution/DB/API design → tasks → test cases), a project-wide traceability matrix, change-impact analysis (including the formal PDF §37 end-to-end demo — raw prompt → full pipeline → change an approved requirement → verify impact analysis identifies the right downstream artifacts — as a repeatable automated test, `Section37DemoTests`), a full AI audit trail, a RAG-grounded Project Copilot (document upload → chunking → embeddings → semantic retrieval → cited, grounded Q&A over project data), an approval lifecycle where Functional Requirements auto-promote to Implemented/Verified as their linked tasks and test cases get approved, a two-version side-by-side diff view for any artifact's history, and an optional Agentic SDLC layer (six agents — Requirements, Analysis, Architecture, Development Planning, QA, Review — chained with per-stage human-approval checkpoints and no autonomous deployment; see `docs/architecture/ADR-003-agent-pipeline.md`) — all as artifacts in one generalized, versioned, approvable model. **Known limitation, not hidden:** no real-browser visual walkthrough of the UI has been done in this environment (no browser-automation tool available here) — the frontend is verified via a clean type-check/build and the API contract it calls, not by eyeballing it running. See [`docs/roadmap/ROADMAP.md`](docs/roadmap/ROADMAP.md).

## Solution layout

```
src/
  AiReap.Domain/          entities, enums — see docs/architecture/ADR-002-data-model.md
  AiReap.Application/     interfaces/services (IAiChatClient, IProjectService, ...)
  AiReap.Infrastructure/  EF Core DbContext, Identity, AI provider implementations
  AiReap.Api/             controllers, auth (JWT), Swagger, Program.cs
web/                       React + TypeScript (Vite)
docs/                      requirements analysis, ADRs, roadmap
```

## Running locally

**Backend.** No credentials are committed: the connection string, the JWT signing key and the Gemini API key come from `dotnet user-secrets` (Development) or environment variables (`ConnectionStrings__Default`, `Jwt__Key`, `Ai__Gemini__ApiKey`). The API refuses to start without the first two, and outside Development it refuses a JWT key shorter than 32 bytes.

```
dotnet user-secrets set "ConnectionStrings:Default" "Server=127.0.0.1,14330;Database=AIReapDb;User Id=sa;Password=<your SQL password>;TrustServerCertificate=True" --project src/AiReap.Api
dotnet user-secrets set "Jwt:Key" "<64+ random characters>" --project src/AiReap.Api
dotnet user-secrets set "Ai:Gemini:ApiKey" "<your Gemini API key>" --project src/AiReap.Api
dotnet ef database update --project src/AiReap.Infrastructure --startup-project src/AiReap.Api
dotnet run --project src/AiReap.Api
```

**AI provider.** Google Gemini (`Ai:Provider = Gemini`, model `Ai:Gemini:Model`, default `gemini-3.6-flash`) is the real LLM, called through the `IAiChatClient` seam with JSON output enforced by the provider, semantic validation of every response, one automatic repair attempt, and retry/backoff/timeout for transient failures. Embeddings for RAG use `gemini-embedding-001` with the same key. See [`docs/GEMINI_SETUP.md`](docs/GEMINI_SETUP.md) for setup, quotas, error codes and the manual live-verification script. In Development without a key, AI calls use a canned stub (recorded as model `stub-canned-model` in the audit trail); outside Development a missing key is a clear 503, never stub data.

**Users and roles.** Public sign-up (`/register`) never grants a role: the account can sign in but every endpoint refuses it until an Administrator assigns one under **User management** (`/admin/users`, Administrator only; enforced by the API, not just the UI). The first Administrator is created at startup from configuration, and only if no Administrator exists yet:

```
dotnet user-secrets set "Bootstrap:AdminEmail" "you@company.com" --project src/AiReap.Api
dotnet user-secrets set "Bootstrap:AdminPassword" "<a strong password>" --project src/AiReap.Api
```

(or the `Bootstrap__AdminEmail` / `Bootstrap__AdminPassword` environment variables). There is no default credential; without these and without an existing Administrator the API logs a warning and creates nothing. Once an Administrator exists the settings are ignored, so remove the password from configuration afterwards. Role changes and deactivation take effect immediately: the API re-checks the user's security stamp on every request, so their existing tokens stop working and they must sign in again. Tokens issued before this check existed carry no stamp and are rejected once, so everyone signs in again after upgrading.

The database is **not** created or migrated automatically on startup — run `dotnet ef database update` (above) after every pull that adds a migration. The agent pipeline needs the `AddAgentRuns` and `AgentConcurrencyGuards` migrations; without them `/api/agent-runs` fails with "Invalid object name 'AgentRuns'". Project-level access control needs the `AddProjectMembers` migration (it also backfills every existing user into every existing project, so upgrading doesn't lock anyone out — see its migration file for the exact backfill).

**Tests** — `dotnet test AiReap.sln`. These are integration tests: each one creates a throwaway `AiReapTests_<guid>` database on the SQL Server, applies the real migrations, and drops it afterwards. The server comes from the `AIREAP_TEST_SQL_CONNECTION` environment variable (server-level, no `Database=`; this is how CI points at its own throwaway container — see `.github/workflows/ci.yml`) or, if that is unset, from your `ConnectionStrings:Default` user-secret with the database name removed. Automated tests never call a live AI API: chat uses the stub and every provider key is blanked in the test host. If the API is running, `dotnet build`/`test` can't overwrite its DLLs — stop it first, or build with `-p:OutDir=<other dir>`. The test host deliberately strips any local `dotnet user-secrets` for `AiReap.Api` before it boots, so a developer's own `Bootstrap:AdminEmail`/`AdminPassword` (set above) never leaks into a test run.

Swagger UI is served at `/swagger` in Development. `GET /api/ai/status` reports which provider/model is active and whether the key and model are usable (it never runs a generation).

**Frontend:**

```
cd web
npm install
npm run dev
```

Defaults to calling the API at `http://localhost:5299` (see `web/.env.development`).
