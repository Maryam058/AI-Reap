# ADR-001: Technology Decisions for Open Questions

Status: Accepted
Date: 2026-09-17

Resolves the Open Questions raised in `docs/requirements/REQUIREMENTS-ANALYSIS.md`. The PDF fixes the stack (React/TS, ASP.NET Core, SQL Server) but leaves these choices open; decisions below are made to unblock Phase 0, consistent with §30's requirement that the AI provider be swappable without rewriting business logic.

## 1. LLM Provider

**Decision:** Anthropic Claude (Messages API) as the first concrete provider, called directly via `HttpClient` (no third-party SDK dependency — the Anthropic .NET SDK isn't first-party/stable enough to depend on yet).

Implemented entirely behind `IAiChatClient` (`AiReap.Application.Ai`). A `StubAiChatClient` is registered when no API key is configured, so the app runs and is demoable without a live key. Swapping to OpenAI/Azure OpenAI later means adding one new `IAiChatClient` implementation — zero changes to `Application` services.

## 2. RAG Vector Store & Embedding Provider

**Decision (embedding provider, resolved 2026-09-17):** OpenAI `text-embedding-3-small`, called via plain `HttpClient` (`OpenAiEmbeddingClient : IEmbeddingClient`), same pattern as `AnthropicAiChatClient`. Required because Anthropic — the chosen chat provider — has no embeddings endpoint, so RAG needs a second vendor regardless of which one is picked; `text-embedding-3-small` was chosen for cost and being well-supported. A `StubEmbeddingClient` (deterministic hashed-bag-of-words vectors) is registered when no `Ai:OpenAI:ApiKey` is configured, so the app still runs and the pipeline is exercisable end-to-end without a live key — same philosophy as `StubAiChatClient`, though unlike the chat stub its output is not semantically meaningful, only structurally valid.

**Decision (vector store):** No separate vector store/index and no `IVectorStore` interface. `DocumentChunk.Embedding` stores the packed `float[]` (`varbinary`) in SQL Server; similarity is a brute-force in-process cosine comparison (`EmbeddingCodec.CosineSimilarity`) over a project's chunks, done directly in `CopilotService`. This is adequate at the scale this tool operates at (a project's own uploaded specs/notes — dozens of chunks, not a web-scale corpus), and a dedicated `IVectorStore` abstraction over a single two-line scan would be premature. Revisit (native SQL Server `VECTOR` type / ANN index, or an external store like Azure AI Search) only if real usage shows a project's chunk count large enough that brute-force scanning is measurably slow.

**Chunking:** fixed-size sliding window (1200 chars, 200 overlap) over already-extracted plain text — see `DocumentService`. Not sentence/paragraph-aware; revisit only if this is shown to split requirements mid-thought in practice.

## 3. Authentication

**Decision:** ASP.NET Core Identity (EF Core store, in the same SQL Server database) + JWT bearer tokens for API auth. No external IdP — keeps the whole system self-contained in SQL Server per the spec's persistence model, and role management (5 fixed roles) is simple enough not to need Entra/Auth0. Roles are seeded at startup: Administrator, BusinessAnalyst, Developer, QA, Reviewer.

## 4. File Parsing (TXT/PDF/DOCX upload)

**Decision (implemented 2026-09-17):** TXT trivial (read as-is). PDF via `UglyToad.PdfPig` (MIT-licensed, no native deps) — text-layer extraction per page, no OCR, so a scanned/image-only PDF yields no text and the upload is rejected with a clear error rather than silently creating an empty source. DOCX via `DocumentFormat.OpenXml` (Microsoft-maintained) — paragraph text only; tables, headers/footers, and embedded objects are out of scope for this pass. Implemented as `DocumentTextExtractor : IDocumentTextExtractor` in `AiReap.Infrastructure.Files`, shared by both upload endpoints that accept files (`requirement-sources/upload` §6 and `documents/upload` §26) so the same extension/extraction logic isn't duplicated.

## 5. Hosting / Deployment Target

**Decision:** Not fixed yet — out of scope for a learning/demo project until a real deployment is needed. Local development targets `dotnet run` + SQL Server (LocalDB or Developer edition) + Vite dev server. Revisit if/when the project needs to be deployed somewhere real.

## 6. Multi-Tenancy

**Decision:** Single-tenant, multi-project, as assumed in the original analysis. Every project is isolated at the data level via a `ProjectId` foreign key, not via separate databases/schemas. Revisit only if a real multi-organization requirement appears.

## 7. Generalized Artifact Data Model

See `ADR-002-data-model.md`.
