# AI-REAP

AI Requirements Engineering & SDLC Automation Platform — React + TypeScript frontend, ASP.NET Core Web API backend, SQL Server persistence, LLM/RAG-powered requirements engineering assistant.

Not a chatbot: a system of record for projects, requirements, versions, relationships, approvals, AI-generated artifacts, and traceability, with AI assisting a human-approved SDLC workflow (raw requirement → clarification → structured requirements → design → tasks → tests → traceability).

See [`docs/README.md`](docs/README.md) for the full requirements analysis, architecture decisions, traceability matrix, and phased development roadmap.

**Status:** Phases 0-4 are complete and the PDF §38 Definition of Done release gate is closed (2026-09-23) — 47/47 integration tests passing against a real SQL Server, CI enforcing build+test on every push/PR. Built and verified: auth/roles (including full administrator-managed user/role management and project-level membership, with a dedicated UI for both), full project management (CRUD, enforced status lifecycle, stakeholders, members), requirement intake (TXT/PDF/DOCX), dashboard, the full AI generation pipeline (analysis → clarification → FR/NFR → business rules → quality/conflict analysis → user stories → acceptance criteria → solution/DB/API design → tasks → test cases), a project-wide traceability matrix, change-impact analysis (including the formal PDF §37 end-to-end demo — raw prompt → full pipeline → change an approved requirement → verify impact analysis identifies the right downstream artifacts — as a repeatable automated test, `Section37DemoTests`), a full AI audit trail, a RAG-grounded Project Copilot (document upload → chunking → embeddings → semantic retrieval → cited, grounded Q&A over project data), an approval lifecycle where Functional Requirements auto-promote to Implemented/Verified as their linked tasks and test cases get approved, a two-version side-by-side diff view for any artifact's history, and an optional Agentic SDLC layer (six agents — Requirements, Analysis, Architecture, Development Planning, QA, Review — chained with per-stage human-approval checkpoints and no autonomous deployment; see `docs/architecture/ADR-003-agent-pipeline.md`) — all as artifacts in one generalized, versioned, approvable model. **Known limitation, not hidden:** no real-browser visual walkthrough of the UI has been done in this environment (no browser-automation tool available here) — the frontend is verified via a clean type-check/build and the API contract it calls, not by eyeballing it running. See [`docs/roadmap/ROADMAP.md`](docs/roadmap/ROADMAP.md).

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

**Backend** — needs SQL Server reachable at the connection string in `src/AiReap.Api/appsettings.json` (the single connection string used in every environment: the Docker SQL Server container on `127.0.0.1,14330`, database `AIReapDb`; override with the `ConnectionStrings__Default` environment variable if yours differs):

```
dotnet user-secrets set "Jwt:Key" "<a long random string>" --project src/AiReap.Api
dotnet ef database update --project src/AiReap.Infrastructure --startup-project src/AiReap.Api
dotnet run --project src/AiReap.Api
```

**Users and roles.** Public sign-up (`/register`) never grants a role: the account can sign in but every endpoint refuses it until an Administrator assigns one under **User management** (`/admin/users`, Administrator only; enforced by the API, not just the UI). The first Administrator is created at startup from configuration, and only if no Administrator exists yet:

```
dotnet user-secrets set "Bootstrap:AdminEmail" "you@company.com" --project src/AiReap.Api
dotnet user-secrets set "Bootstrap:AdminPassword" "<a strong password>" --project src/AiReap.Api
```

(or the `Bootstrap__AdminEmail` / `Bootstrap__AdminPassword` environment variables). There is no default credential; without these and without an existing Administrator the API logs a warning and creates nothing. Once an Administrator exists the settings are ignored, so remove the password from configuration afterwards. Role changes and deactivation take effect immediately: the API re-checks the user's security stamp on every request, so their existing tokens stop working and they must sign in again. Tokens issued before this check existed carry no stamp and are rejected once, so everyone signs in again after upgrading.

The database is **not** created or migrated automatically on startup — run `dotnet ef database update` (above) after every pull that adds a migration. The agent pipeline needs the `AddAgentRuns` and `AgentConcurrencyGuards` migrations; without them `/api/agent-runs` fails with "Invalid object name 'AgentRuns'". Project-level access control needs the `AddProjectMembers` migration (it also backfills every existing user into every existing project, so upgrading doesn't lock anyone out — see its migration file for the exact backfill).

**Tests** — `dotnet test AiReap.sln`. These are integration tests: each one creates a throwaway `AiReapTests_<guid>` database on the SQL Server in `TestHost.ServerConnection` (defaults to the Docker dev container on `127.0.0.1,14330`), applies the real migrations, and drops it afterwards. Set the `AIREAP_TEST_SQL_CONNECTION` environment variable if your SQL Server differs (this is how CI points at its own service container — see `.github/workflows/ci.yml`). If the API is running, `dotnet build`/`test` can't overwrite its DLLs — stop it first, or build with `-p:OutDir=<other dir>`. The test host deliberately strips any local `dotnet user-secrets` for `AiReap.Api` before it boots, so a developer's own `Bootstrap:AdminEmail`/`AdminPassword` (set above) never leaks into a test run.

Swagger UI is served at `/swagger` in Development. Without an `Ai:Anthropic:ApiKey` configured, AI calls use a canned stub response so the app is fully runnable with no external dependency; set the key (via user-secrets) to call a real model. Likewise, without an `Ai:OpenAI:ApiKey`, document embeddings use a deterministic (non-semantic) stub, so the RAG/Copilot pipeline is exercisable but not actually grounded in meaning — set the key for real semantic retrieval.

**Frontend:**

```
cd web
npm install
npm run dev
```

Defaults to calling the API at `http://localhost:5299` (see `web/.env.development`).
