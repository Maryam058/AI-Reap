# Gemini setup and live verification

AI-REAP's real LLM is **Google Gemini**, reached through the same `IAiChatClient` seam every
generator uses (`GeminiAiChatClient`, selected by `Ai:Provider = Gemini`). Nothing outside
`AiReap.Infrastructure/Ai` knows which provider is in use.

```
Generator service ─► GenerationSupport.CallAiAndParseAsync
                        │  (prompt with required JSON shape)
                        ▼
                     IAiChatClient = ResilientAiChatClient ─► GeminiAiChatClient ─► Gemini API
                        │  timeout · retry with backoff (429/5xx/network/timeout) · error classification
                        ▼
                     AiJsonParser: JSON shape ─► semantic validation (IValidatableAiResponse)
                        │  invalid? one repair attempt with the errors fed back, else reject (nothing saved)
                        ▼
                     Artifacts + versions + relationships + AIExecution audit row
```

## 1. Configure

1. Create an API key at <https://aistudio.google.com/apikey> (free tier is enough).
2. Store it **only** in local configuration — never in `appsettings*.json`, docs, tests or commits:

   ```
   dotnet user-secrets set "Ai:Gemini:ApiKey" "<your key>" --project src/AiReap.Api
   ```

   or, outside Development, the environment variable `Ai__Gemini__ApiKey`.
3. Optional overrides (defaults in `src/AiReap.Api/appsettings.json`):

   | Setting | Default | Notes |
   |---|---|---|
   | `Ai:Provider` | `Gemini` | `Anthropic`/`Ollama` implementations still exist but are not required. |
   | `Ai:Gemini:Model` | `gemini-3.6-flash` | Any `generateContent` model your key can use. Google no longer grants `gemini-2.5-*` to new users. |
   | `Ai:Gemini:MaxOutputTokens` | `16384` | Includes thinking tokens. Truncated output is reported as `ai_invalid_output`, never saved. |
   | `Ai:Gemini:ThinkingLevel` | `low` | Gemini 3 thinking effort (`minimal`/`low`/`medium`/`high`; Gemini 3 cannot turn thinking off). Empty uses the model default. |
   | `Ai:Gemini:ThinkingBudget` | _(unset)_ | Legacy Gemini 2.5 setting; ignored while `ThinkingLevel` is set (Gemini rejects both together). |
   | `Ai:Gemini:Temperature` | _(unset)_ | Unset uses the model default (1.0), as Google recommends for Gemini 3. |
   | `Ai:Gemini:EmbeddingModel` / `EmbeddingDimensions` | `gemini-embedding-001` / `768` | RAG embeddings. Changing either makes existing chunks stale; run `POST /api/projects/{id}/documents/reindex`. |
   | `Ai:Resilience:TimeoutSeconds` | `120` | Per attempt. |
   | `Ai:Resilience:MaxAttempts` | `3` | Transient failures only. |
   | `Ai:Resilience:MaxRetryAfterSeconds` | `30` | A longer provider back-off (e.g. daily quota) fails fast with 429 instead of holding the request open. |

4. Restart the API and check `GET /api/ai/status` — it calls Gemini's `models.get` (no generation,
   no generation quota) and reports whether the key is accepted and the model exists.

The free tier has per-minute and per-day request limits set by Google (they change over time; see
<https://ai.google.dev/gemini-api/docs/rate-limits>). Hitting them returns HTTP 429 from AI-REAP with
`code: ai_rate_limited` and a `Retry-After` header when Google provides one.

## 2. Error responses

AI failures are no longer a generic 500. Every one is RFC 7807 ProblemDetails with a stable `code`
and a `retryable` flag (`ApplicationExceptionHandler`); the frontend shows the `title` + `detail`.

| HTTP | `code` | Meaning |
|---|---|---|
| 503 | `ai_not_configured` | No usable Gemini key (outside Development, where the stub is not allowed). |
| 502 | `ai_auth_failed` | Google rejected the key. |
| 429 | `ai_rate_limited` | Rate limit / free-tier quota. Retryable. |
| 504 | `ai_timeout` | No response within `Ai:Resilience:TimeoutSeconds` (after retries). Retryable. |
| 503 | `ai_unavailable` | Gemini 5xx or network failure (after retries). Retryable. |
| 502 | `ai_request_rejected` | Google rejected the request (unknown model, unsupported region, billing). `detail` carries Google's reason. |
| 422 | `ai_content_blocked` | Gemini safety filters blocked the prompt or response. |
| 502 | `ai_invalid_output` | Output failed JSON/semantic validation even after the repair attempt; `errors` lists why. Nothing was saved. |

Logs record operation, model, input reference, failure kind, provider status and validation errors —
never the API key (it is sent only in the `x-goog-api-key` header and redacted from any provider
message) and never the prompt or response body.

## 3. Automated tests vs. live verification

* `dotnet test` **never** calls Gemini: `TestHost` replaces `IAiChatClient` with the deterministic stub
  and blanks every provider key. Gemini request/response handling, error mapping, retry and timeout
  are covered by `GeminiAiChatClientTests` / `ResilientAiChatClientTests` with a faked HTTP transport.
* The live path is verified manually with `scripts/verify-gemini.ps1` against a running API:

  ```
  dotnet run --project src/AiReap.Api          # in one terminal
  $env:AIREAP_VERIFY_EMAIL = "you@company.com"   # a Business Analyst or Administrator
  $env:AIREAP_VERIFY_PASSWORD = "<password>"
  ./scripts/verify-gemini.ps1 -ApiBaseUrl http://localhost:5299
  ```

  It checks `/api/ai/status`, then runs analyze → generate-requirements → traceability → Copilot
  on a new project and confirms the audit trail shows the Gemini model (not the stub). It uses about
  four chat requests and a handful of embedding requests.

## 4. `/api/requirement-sources/{id}/analyze` — expected successful response

HTTP 200:

```json
{
  "actors": ["Employee", "Manager"],
  "capabilities": ["Submit leave request", "Approve or reject leave request"],
  "dataElements": ["Leave type", "Start date", "End date", "Status"],
  "notes": ["..."],
  "clarificationQuestions": [
    {
      "id": "…", "code": "CQ-001", "title": "Approval hierarchy",
      "status": 0, "origin": 0, "currentVersion": 1,
      "data": { "question": "Is multi-level approval required?", "reason": "…", "clarificationStatus": "Open" }
    }
  ]
}
```

Each clarification question is persisted as an `AiGenerated` artifact (v1) with an `AIExecution`
audit row whose `model` is the configured Gemini model.
