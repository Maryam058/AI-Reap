# Gemini setup and live verification

> **Superseded for chat (2026-10-21).** Anthropic Claude is now the only chat provider and Gemini/Groq
> are no longer used for chat or as a chat fallback — see [`CLAUDE_SETUP.md`](CLAUDE_SETUP.md). The
> `GeminiAiChatClient`/`GroqAiChatClient` classes remain in the codebase but are not registered. Gemini is
> still used for **embeddings** (`gemini-embedding-001`) when `Ai:Gemini:ApiKey` is set. The rest of this
> document describes the previous chat setup and is kept for reference.

AI-REAP's primary LLM is **Google Gemini**, with **Groq** as an automatic fallback, both reached
through the same `IAiChatClient` seam every generator uses (`GeminiAiChatClient` selected by
`Ai:Provider = Gemini`; `GroqAiChatClient` by `Ai:FallbackProvider = Groq`). Nothing outside
`AiReap.Infrastructure/Ai` knows which provider is in use.

```
Generator service ─► GenerationSupport.CallAiAndParseAsync
                        │  (prompt with required JSON shape)
                        ▼
                     IAiChatClient = FallbackAiChatClient
                        ├─► ResilientAiChatClient ─► GeminiAiChatClient ─► Gemini API   (always first)
                        │      429 / quota / 5xx / network / timeout?
                        └─► RequestBudgetAiChatClient ─► ResilientAiChatClient ─► GroqAiChatClient ─► Groq API  (fallback;
                               condenses prompts over Ai:Groq:MaxRequestTokens)
                        │  per-attempt timeout: Gemini Ai:Gemini:TimeoutSeconds (300s, not resent by default), others Ai:Resilience:TimeoutSeconds (120s)
                        │  retry with backoff (5xx/network; timeouts except Gemini's; no 429 retry on Gemini) · error classification
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
3. Optional overrides. "Default" is the value in the shipped `src/AiReap.Api/appsettings.json`; settings
   marked _(code default)_ are not in that file and take the value in their options class.

   | Setting | Default | Notes |
   |---|---|---|
   | `Ai:Provider` | `Gemini` | Primary provider. `Groq` or `Anthropic` also accepted. |
   | `Ai:FallbackProvider` | `Groq` | Used automatically when the primary is rate-limited/out of quota/unavailable. `None` disables it. |
   | `Ai:PrimaryCooldownSeconds` | `600` | After a Gemini 429, skip Gemini (go straight to Groq) for Google's retry delay, or this long for a daily/zero quota; capped at this value. |
   | `Ai:Gemini:Model` | `gemini-3.8-flash` | Any `generateContent` model your key can use. Google no longer grants `gemini-2.5-*` to new users. |
   | `Ai:Gemini:MaxOutputTokens` | `65536` | Includes thinking tokens. Truncated output is reported as `ai_invalid_output`, never saved. The code default, used only when the setting is absent, is `16384`. |
   | `Ai:Gemini:TimeoutSeconds` | `300` | Per-attempt timeout for Gemini only; replaces `Ai:Resilience:TimeoutSeconds` for Gemini. `0` or negative uses `Ai:Resilience:TimeoutSeconds`. |
   | `Ai:Gemini:MaxTimeoutAttempts` | `1` | Total attempts when a Gemini call times out. `1` = a timed-out request is not resent (Google has usually processed and counted it already); with a fallback configured the call goes to the fallback provider instead, otherwise it fails with `504 ai_timeout`. `0` or negative uses `Ai:Resilience:MaxAttempts`; capped at `Ai:Resilience:MaxAttempts`. 429, 5xx and network failures keep the `Ai:Resilience` rules. |
   | `Ai:Gemini:LargeGenerationWarningTokens` | `30000` _(code default)_ | Dry-run AI plan only: Generate for a source estimated above this many tokens and sent to Gemini whole is flagged `LargeResponseRisk` (may hit `MaxOutputTokens` or `TimeoutSeconds`). A size heuristic; it never blocks a request. `0` or negative disables it. |
   | `Ai:Gemini:BaseUrl` | `https://generativelanguage.googleapis.com/` _(code default)_ | Base address for Gemini chat, embedding and `/api/ai/status` requests. |
   | `Ai:Gemini:ThinkingLevel` | `low` | Gemini 3 thinking effort (`minimal`/`low`/`medium`/`high`; Gemini 3 cannot turn thinking off). Empty uses the model default. |
   | `Ai:Gemini:ThinkingBudget` | _(unset)_ | Legacy Gemini 2.5 setting; ignored while `ThinkingLevel` is set (Gemini rejects both together). |
   | `Ai:Gemini:Temperature` | _(unset)_ | Unset uses the model default (1.0), as Google recommends for Gemini 3. |
   | `Ai:Gemini:EmbeddingModel` / `EmbeddingDimensions` | `gemini-embedding-001` / `768` | RAG embeddings. Changing either makes existing chunks stale; run `POST /api/projects/{id}/documents/reindex`. |
   | `Ai:Resilience:TimeoutSeconds` | `120` | Per attempt, for Groq and Anthropic. Gemini uses `Ai:Gemini:TimeoutSeconds` unless that is `0` or negative. |
   | `Ai:Resilience:MaxAttempts` | `3` | Transient failures only. |
   | `Ai:Resilience:MaxTimeoutAttempts` | `0` _(code default)_ | Total attempts on a timeout for Groq and Anthropic; `0` uses `MaxAttempts`. Not used for Gemini (see `Ai:Gemini:MaxTimeoutAttempts`). |
   | `Ai:Resilience:BaseDelayMilliseconds` | `1000` | Backoff before retry *n*: `BaseDelayMilliseconds × 2^(n-1)` plus random jitter of up to `BaseDelayMilliseconds`, capped at `MaxDelaySeconds`. A provider's retry hint is used instead when given, except that a 429 always waits at least this backoff. |
   | `Ai:Resilience:MaxDelaySeconds` | `20` | Cap on the computed backoff. A provider's retry hint is limited by `MaxRetryAfterSeconds` instead. |
   | `Ai:Resilience:MaxRetryAfterSeconds` | `30` | A longer provider back-off (e.g. daily quota) fails fast with 429 instead of holding the request open. |
   | `Ai:Resilience:MaxRateLimitAttempts` | `2` | Total attempts on a 429 (one retry, after Google's `retryDelay` when given). A daily or zero-limit quota (`QuotaFailure` per-day quota id, or `limit: 0`) is never retried. |
   | `Ai:Copilot:MaxQuestionChars` | `2000` _(code default)_ | Longest Copilot question accepted. A longer one is rejected with `400 validation_failed` before anything is read or sent; it is never shortened. `0` or negative uses `2000`. The UI reads the limit from `GET /api/projects/{id}/copilot/limits`. |

4. Restart the API and check `GET /api/ai/status` — it calls Gemini's `models.get` (no generation,
   no generation quota) and reports whether the key is accepted and the model exists.

The free tier has per-minute and per-day request limits set by Google (they change over time; see
<https://ai.google.dev/gemini-api/docs/rate-limits>). Hitting them returns HTTP 429 from AI-REAP with
`code: ai_rate_limited`, the message "Gemini rate limit reached. Please wait and try again…", a
`Retry-After` header plus `retryAfterSeconds` when Google provides a delay, and `quotaExhausted: true`
when the per-day (or zero) quota is used up, so no automatic retry could help. With Groq configured
this only happens when Groq is exhausted too (see below).

## 1a. Groq fallback

Groq (OpenAI-compatible `openai/v1/chat/completions`, plain `HttpClient`, `response_format:
json_object`) takes over automatically - no config change - when Gemini fails with:

* **HTTP 429 / `RESOURCE_EXHAUSTED`**: per-minute or daily quota. Gemini is **not retried** first, and
  is then skipped for Google's `retryDelay` (daily/zero quota: `Ai:PrimaryCooldownSeconds`).
* **5xx / network failure**: after the normal `Ai:Resilience` retries.
* **Timeout**: after `Ai:Gemini:MaxTimeoutAttempts` attempts of `Ai:Gemini:TimeoutSeconds` each. With the
  shipped values (`1`, `300`), the first Gemini timeout goes straight to Groq without resending.

It does **not** fall back on a rejected/invalid key, missing configuration, a rejected request (bad
model, 400), blocked content, invalid JSON output (that gets the normal repair attempt) or a cancelled
request - those are surfaced as-is. If Groq fails too, the error names both providers and uses Groq's
failure kind (e.g. `429 ai_rate_limited` with the sooner retry hint).

Key: `dotnet user-secrets set "Ai:Groq:ApiKey" "<key>" --project src/AiReap.Api`, or
`Ai__Groq__ApiKey`, or - only when neither is set - the conventional `GROQ_API_KEY` environment variable.

| Setting | Default | Notes |
|---|---|---|
| `Ai:Groq:Model` | `openai/gpt-oss-120b` | 131k context, JSON mode, strongest reasoning model on Groq. Free tier: ~8k tokens/min, 1k requests/day. |
| `Ai:Groq:MaxCompletionTokens` | `16384` | Includes reasoning tokens. Truncation is `ai_invalid_output`, never saved. |
| `Ai:Groq:MaxRequestTokens` | `8000` | Largest single request Groq accepts (the free tier's tokens-per-minute limit; Groq answers 413 above it). Raise on a paid tier; `0` disables. |
| `Ai:Groq:ReservedOutputTokens` | `4000` | Part of `MaxRequestTokens` kept for the response (JSON output + gpt-oss reasoning; 2,000 was too little and truncated responses). Input budget = `MaxRequestTokens - ReservedOutputTokens` (4,000 by default). |
| `Ai:Groq:MaxChunkedParts` | `8` | A source too large for one Groq request is split into parts; a normal Analyze/Generate needing more parts than this is refused before anything is sent. In batch mode it is also the most parts one **Continue** sends. `0` disables the check. |
| `Ai:Groq:MaxBatchedRunParts` | `0` | Batch mode. `0` = off: nothing changes, the batched-run endpoints answer `400 batch_mode_disabled`, and the `AiChunkRuns` tables are never read. `N > 0` = on: normal Analyze/Generate are unchanged (still refused above `MaxChunkedParts`); additionally a batched run of up to `N` parts can be started for the same single requirement source (Start → Continue, at most `MaxChunkedParts` parts per action, finished parts kept → Save). Nothing becomes an artifact before Save. |
| `Ai:Groq:MaxRateLimitAttempts` | `4` | Total attempts on a Groq 429 (overrides `Ai:Resilience:MaxRateLimitAttempts` when higher). Each retry waits at least the exponential backoff, since Groq's sub-second hints rarely clear its per-minute token window. |
| `Ai:Groq:ReasoningEffort` | `low` | gpt-oss `low`/`medium`/`high`; empty omits it (needed for models without this option). |
| `Ai:Groq:Temperature` | _(unset)_ | Model default. |

The AI audit trail (`AIExecution.Model`) records the model that actually answered: `gemini-3.8-flash`,
`groq:openai/gpt-oss-120b`, or `stub-canned-model`. Logs show `AI provider Gemini attempted`,
`... failed (RateLimited ...); falling back to Groq`, `AI fallback provider Groq succeeded` / `also failed`.
`GET /api/ai/status` reports the fallback's key/model check under `fallback`.

In Development with **neither** key, the canned stub is used and a warning says so; it is never used
while either real provider is configured. Only a Groq key → Groq alone (logged).

## 2. Error responses

AI failures are no longer a generic 500. Every one is RFC 7807 ProblemDetails with a stable `code`
and a `retryable` flag (`ApplicationExceptionHandler`); the frontend shows the `title` + `detail`.

| HTTP | `code` | Meaning |
|---|---|---|
| 503 | `ai_not_configured` | No usable Gemini or Groq key (outside Development, where the stub is not allowed). |
| 502 | `ai_auth_failed` | Google rejected the key. |
| 429 | `ai_rate_limited` | Rate limit / free-tier quota. Retryable later; `retryAfterSeconds` and `quotaExhausted` say when. |
| 504 | `ai_timeout` | No response within the per-attempt timeout (Gemini: `Ai:Gemini:TimeoutSeconds`, no resend by default; Groq/Anthropic: `Ai:Resilience:TimeoutSeconds`, after retries). With Groq configured, a Gemini timeout goes to Groq first. Retryable. |
| 503 | `ai_unavailable` | Gemini 5xx or network failure (after retries). Retryable. |
| 502 | `ai_request_rejected` | Google rejected the request (unknown model, unsupported region, billing). `detail` carries Google's reason. |
| 422 | `ai_content_blocked` | Gemini safety filters blocked the prompt or response. |
| 502 | `ai_invalid_output` | Output failed JSON/semantic validation even after the repair attempt; `errors` lists why. Nothing was saved. |

Logs record operation, model, input reference, failure kind, provider status and validation errors —
never the API key (it is sent only in the `x-goog-api-key` header and redacted from any provider
message) and never the prompt or response body.

## 3. Automated tests vs. live verification

* `dotnet test` **never** calls Gemini or Groq: `TestHost` replaces `IAiChatClient` with the deterministic stub
  and blanks every provider key (including `GROQ_API_KEY`). Groq handling and the fallback rules are covered by
  `GroqAiChatClientTests` / `FallbackAiChatClientTests` with a faked HTTP transport. Gemini request/response handling, error mapping, retry and timeout
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
