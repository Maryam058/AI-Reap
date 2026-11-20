# Claude setup (the only chat provider)

AI-REAP sends every chat/LLM request — requirement analysis, requirement generation and every later
lifecycle stage (user stories, acceptance criteria, business rules, quality and conflict review,
design, data, API, tasks, test cases, review agent, copilot) — to **Anthropic Claude** through the
`IAiChatClient` seam. **Gemini and Groq are not chat providers and are never used as a chat fallback.**
Embeddings are selected separately and are unchanged (see §5).

```
Pipeline service ─► GenerationSupport (whole-or-split / whole-or-refuse, one repair attempt)
                        ▼
                     IAiChatClient = RequestBudgetAiChatClient   (lossless application budget; never condenses)
                        ▼
                     ResilientAiChatClient   (Ai:Anthropic:TimeoutSeconds, retries for 429/5xx/network only)
                        ▼
                     AnthropicAiChatClient ─► POST https://api.anthropic.com/v1/messages
                        ▼
                     AiJsonParser: JSON shape ─► semantic validation ─► evidence checks
```

## 1. Configure the API key

The key is read **only** from `Ai:Anthropic:ApiKey`. Never put it in `appsettings*.json`, docs, tests or commits.

```powershell
# Development (user-secrets)
dotnet user-secrets set "Ai:Anthropic:ApiKey" "<your Anthropic API key>" --project src/AiReap.Api
# Any other environment
$env:Ai__Anthropic__ApiKey = "<your Anthropic API key>"
```

The conventional `ANTHROPIC_API_KEY` environment variable is deliberately **not** read, so a developer
machine that has it set for other tools does not silently turn Development's offline stub into real,
billed requests.

| Situation | Behavior |
| --- | --- |
| Usable key | Real Claude client in every environment. |
| No key / placeholder, Development or Testing | Deterministic stub, logged as a warning and recorded as model `stub-canned-model` in the AI audit trail. Never Gemini or Groq. |
| No key / placeholder, any other environment | Each AI call fails **before sending anything** with `AiProviderException(NotConfigured)` → HTTP 503 naming `Ai:Anthropic:ApiKey`. |
| `Ai:Provider` = `Gemini` or `Groq` | Fails with an actionable error ("no longer supported as chat providers … set Ai:Provider to 'Anthropic'"). Never silently honored or replaced. |
| `Ai:FallbackProvider` set | Ignored with a warning; there is no chat fallback. |
| Invalid Anthropic limits (see §2) | Fails resolution listing every problem. |

> **Legacy overrides — check before deploying.** The shipped `appsettings.json` now says `Ai:Provider = Anthropic`,
> but a user-secrets entry or an `Ai__Provider` environment variable that still says `Gemini` or `Groq` overrides it.
> Every service that needs the chat client then fails to resolve (an `InvalidOperationException` naming the fix,
> which most endpoints surface as a generic 500). AI-REAP does not rewrite deployment secrets. Check by setting
> **name** without printing secret values, e.g.
> `dotnet user-secrets list --project src/AiReap.Api | Select-String '^Ai:(Provider|FallbackProvider) '` (shows only
> those two non-secret lines) and `Get-ChildItem Env: | Where-Object Name -like 'Ai__*Provider*' | Select-Object Name`,
> then remove or change the override yourself. A leftover `Ai:FallbackProvider`
> is harmless (ignored with a warning).

## 2. Model and limits

Verified against Anthropic's documentation (models overview, context windows, Messages API) on 2026-10-21:
`claude-sonnet-5-5` has a 1M-token context window and up to 128K output tokens; adaptive thinking is on
by default and **thinking tokens count toward `max_tokens`**.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Ai:Anthropic:Model` | `claude-sonnet-5-5` | Sent and recorded exactly as configured. **An existing explicit override (user-secrets / `Ai__Anthropic__Model`) is preserved.** Only deployments that relied on the old shipped value (`claude-sonnet-5`) move to `claude-sonnet-5-5`. |
| `Ai:Anthropic:MaxTokens` | 16000 | Output ceiling per response, thinking included. Raised from 8192. |
| `Ai:Anthropic:MaxRequestTokens` | 200000 | **Application-side** cap on input + `MaxTokens` per request (input budget 184,000 Claude tokens). Deliberately far below the 1M context window: bounded cost/latency per request and less long-context degradation. |
| `Ai:Anthropic:EstimatedCharsPerToken` | 2.5 | The budget is enforced with an offline estimate (~3 chars/token). Claude's current tokenizer averages ~2.5 chars/token (1M tokens ≈ 2.5M characters), so the budget is scaled by 2.5/3 to stay conservative. |
| `Ai:Anthropic:ContextWindowTokens` | 1000000 | Used only to validate `MaxRequestTokens`. |
| `Ai:Anthropic:MaxChunkedParts` | 8 | Most parts a source may be split into **for the input budget** (analysis); more is refused before anything is sent. |
| `Ai:Anthropic:GenerationPartSourceTokens` | 6000 | Staged requirement generation (§3): a source above this many source tokens (estimate units, ~3 characters each; ~18,000 characters) is generated in parts of at most this size. `0` = off (one response). |
| `Ai:Anthropic:MaxGenerationParts` | 24 | Most parts staged generation may use in total, including re-splits after a truncated answer. |
| `Ai:Anthropic:TimeoutSeconds` / `MaxTimeoutAttempts` | 600 / 1 | Per-attempt timeout; a timed-out request is **not** resent automatically. |
| `Ai:Anthropic:Effort` | empty | `output_config.effort` (`low`…`max`); empty = model default (`high`). |
| `Ai:Anthropic:UseTokenCountingApi` | false | See §3. |

## 3. Large SRS documents

The previously observed SRS was estimated at **~84,308 input tokens** for the whole analysis request
(it was refused against Groq's 4,000-token budget). With the defaults it is sent to Claude **whole, in
one request** for analysis, with its text unchanged (clean-up applies only to a prompt that is over
budget) — the input budget in estimate units is ~153,333. Requirement **generation** of the same source
is staged (below), because its answer would not fit one response.

- **Nothing is ever condensed.** Requirement analysis and generation split an over-budget source at
  section/paragraph boundaries into parts that together carry every line (`GenerationSupport.CallAiAndParseChunkedAsync`);
  every later stage sends its full prompt — **including the complete original SRS text**, not just an
  analysis summary — whole or refuses it before sending (`AiStageOverBudgetException`).
- A source needing more than `MaxChunkedParts` parts, or whose clarifications fit no part, is refused
  before anything is sent; nothing is saved.
- **Opt-in precise counting** (`UseTokenCountingApi=true`): when the offline estimate says "over budget",
  `POST /v1/messages/count_tokens` is asked first and a request it accepts is sent whole. Overhead and
  limits: one extra HTTPS round trip per over-budget request; free, but with its own requests-per-minute
  rate limit; the result is itself an estimate and may include system-added tokens (not billed); a failed
  count falls back to the offline estimate. It is off by default because the dry-run AI plan never calls a
  provider and would otherwise disagree with a real run. When it is on, refusal messages that say
  "nothing was sent" mean no **generation** request was sent — the count request itself was.

### Output limits are separate from input limits

A response that stops at `max_tokens` or `model_context_window_exceeded` is rejected as truncated
(`AiOutputTruncatedException`; no repair attempt — the same limit would cut it off again) and its partial
content is never used. Malformed or invalid JSON gets exactly one repair attempt with the validation
errors, then the operation is rejected. Nothing is saved in either case.

### Staged requirement generation

**Why.** Generation answers with far more tokens than the source it covers: every requirement carries
title, actor, processing, expected result, IDs, origin and 1–2 verbatim evidence quotes. On the
requirement-dense golden fixture the full answer is ~5× the source (estimate units); ordinary SRS prose is
less dense (~2×). Adaptive thinking (default effort `high` on Claude Sonnet 5.5) is billed as output and
shares `MaxTokens`. An ~84k-token SRS therefore cannot be answered in one 16,000-token response.

**Why not just raise `MaxTokens`.** Requests are non-streaming. A much larger `max_tokens` means
multi-minute single responses (Anthropic recommends streaming or batches for requests over ~10 minutes;
idle connections can be dropped), a longer `TimeoutSeconds`, and a larger billed loss whenever a long
answer is cut off or times out — and it still has a ceiling (128K). Bounded parts keep each response small,
retries cheap, and failures local. Lowering `Ai:Anthropic:Effort` (e.g. `medium`) reduces thinking tokens
and is a further lever; it is not changed by default because it can affect quality.

**How** (`GenerationSupport.CallAiAndParseChunkedAsync` with `stageOutput`, used by requirement generation only):

1. **Plan** (`ChunkPlanner.PlanStaged`, identical in the dry-run AI plan): a source within
   `GenerationPartSourceTokens` whose prompt fits is sent whole, exactly as before. A larger one is split up
   front at section/paragraph boundaries into parts of at most that size; every passage goes to exactly one
   part, and each part carries the objectives, excerpts and the clarifications relevant to it. Needing more
   than `MaxGenerationParts` parts is refused **before anything is sent**.
2. **Truncated answer → split again.** If the whole-source answer or one part's answer is cut off, it is not
   used; that source text is split in half and re-sent as smaller parts (never below 500 tokens, never over
   `MaxGenerationParts` in total). If it cannot be split further, the run fails, saying **how many requests were
   already sent** (they may be billed) and that nothing was saved.
3. **Empty part check** (`StagedPartCoverage`). When a part's answer contains no requirements, the IDs its
   text defines (a line starting `FR-012:`; table-of-contents lines and traceability-matrix rows don't count)
   are checked. An ID is covered only if a generated requirement cites it **and** quotes its own passage
   (a mere citation such as "see FR-250" is not coverage). An uncovered ID whose family (`FR`, `SEC`, `UC`, …)
   the run did generate elsewhere stops the run with nothing saved: `502 ai_invalid_output`,
   `retryable: false`, naming the parts and IDs (`failedParts`) and the AI requests sent (`requestsSent`;
   automatic retries after connection errors are not included, and sent requests may be billed). The run
   stops as soon as the gap is certain (no later part mentions the ID), otherwise after the last part.
   Generating again helps only if the AI left them out by chance; IDs that are not requirements (removed,
   out of scope) fail every time, so edit the source or move them out. Uncovered IDs of a family generated
   nowhere (glossaries, standards and revision tables, test-case lists, and a requirement family that exists
   only in that one part) don't stop the run; they are logged and listed as `uncovered` in `sourceIdCoverage`.
4. **Consolidation** (`RequirementGenerationService.Merge`): requirements identical in every content field are
   merged (their IDs, references and evidence united); everything else is kept. Then a **cross-part review**
   is returned in `completeness`:
   - `crossPartSharedSourceIds` — source IDs cited by different requirements from different parts (possible
     duplicates or conflicting restatements);
   - `crossPartTargetConflicts` — NFRs from different parts with the same category and title but different targets.
   Nothing is merged, rewritten or dropped on these; a reviewer decides. **Missing** requirement IDs are reported
   by the existing `sourceIdCoverage` (`incomplete` + the uncovered IDs) after saving.
5. Evidence quotes are verified against the full original SRS as before, so each requirement keeps its
   source wording and IDs; unsupported numeric targets are still flagged (`targetSupport`, `unsupportedNumbers`).

**Cost and latency.** Parts run sequentially. With the defaults an ~84k-token SRS is ~14 parts: ~14 requests
of ~6–8k input tokens each plus their outputs, roughly 1–2 minutes each, so expect tens of minutes for the
largest documents. Each truncated answer is an extra billed request. The analysis stage is not staged (its
answers are short); a truncated analysis answer is still rejected, never accepted.

## 4. Errors

| Condition | AiFailureKind (API problem code) | Retried? |
| --- | --- | --- |
| 401 invalid key, 403 permission | Authentication (`ai_auth_failed`, 502) | No |
| 402 billing, 400 credit balance / spend limit | RequestRejected (billing message) | No |
| 400 "prompt is too long" | RequestRejected (oversized input) | No |
| 404 unknown model | RequestRejected naming `Ai:Anthropic:Model` | No |
| 413 request too large | RequestRejected | No |
| 429 rate limit | RateLimited (honors `retry-after`) | Up to `Ai:Resilience:MaxRateLimitAttempts` |
| 429 without `retry-after` mentioning a spend cap | RateLimited, quota exhausted | No |
| 500 / 529 overloaded | Unavailable | Up to `Ai:Resilience:MaxAttempts` |
| 504 / our own timeout | Timeout | Not resent by default |
| `stop_reason: refusal` | ContentBlocked (category, explanation) | No |
| truncated / unexpected stop reason / empty / invalid envelope | Output validation | No (invalid JSON text: one repair) |

Every message carries Anthropic's `request-id` when one was returned and is scrubbed of anything
key-shaped. For rejected requests the API response `detail` shows the same actionable summary (billing,
`Ai:Anthropic:Model`, context window, request size) followed by the provider's sanitized detail.

The API says an automatic correction was attempted **only** when the one repair attempt really ran and
also failed; client-side failures (truncation, an empty or malformed envelope) say no correction was
attempted. Refusals made before sending point to the setting to review (`Ai:Anthropic:MaxRequestTokens`,
`MaxChunkedParts`, `MaxGenerationParts`) instead of suggesting another provider; the old "retry when an AI
provider without this limit is available" advice appears only when a real primary/fallback route exists.
The API's timeout response warns that the request may still be billed.

**Timeouts may still be billed.** A request that timed out (504 from Anthropic, or AI-REAP's own
per-attempt timeout) had usually already been received and may still be processed and charged by
Anthropic. That is why `MaxTimeoutAttempts` defaults to 1.

**Policy requiring review — no fallback on 401/403.** Authentication and permission failures are
surfaced to the operator and are never retried or routed to another provider (there is none). This is
the implemented behavior, not an approved decision; confirm it is acceptable for your deployment.

**Server-side refusal fallback is not enabled.** Anthropic offers an opt-in `fallbacks` parameter that
re-serves a refused request on another Claude model. It is not sent, because it would change which model
produced an artifact; enable it only if that attribution trade-off is acceptable.

## 5. Embeddings are unchanged

Embeddings no longer depend on `Ai:Provider`. `Ai:EmbeddingProvider` (`Gemini`, `OpenAI`, `Stub`) wins
when set; empty keeps the automatic choice: **Gemini** (`gemini-embedding-001@768`) when
`Ai:Gemini:ApiKey` is usable, else OpenAI when `Ai:OpenAI:ApiKey` is set, else the stub. Stored
`DocumentChunk.EmbeddingModel` values keep matching, so no reindex is needed.

> **Check before upgrading:** a deployment that previously ran *Anthropic chat with a Gemini key also
> configured* used OpenAI or stub embeddings (the old rule required `Ai:Provider = Gemini`). It now gets
> Gemini embeddings unless it sets `Ai:EmbeddingProvider` to `OpenAI` or `Stub`.

## 6. Verifying

`GET /api/ai/status` reports provider, model and whether a usable key is configured; it never sends a
request to Anthropic, so it cannot confirm the key is accepted — the first real call does. The offline
tests (`AnthropicAiChatClientTests`, `AiChatClientSelectionTests`, `ClaudeLargeSrsPipelineTests`) cover
the client, selection and the large-SRS paths over a fake HTTP transport.
