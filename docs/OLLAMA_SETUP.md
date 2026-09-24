# Running AI-REAP locally with Ollama (no Anthropic API charges)

AI-REAP's AI features (Copilot, requirement analysis/generation, and all six pipeline agents)
go through a single abstraction, `IAiChatClient`. Two real implementations exist:

- `AnthropicAiChatClient` — calls the Anthropic API. Requires `Ai:Anthropic:ApiKey` and costs
  real money per call.
- `OllamaAiChatClient` — calls a locally-running [Ollama](https://ollama.com) instance. No API
  key, no per-call cost. Uses your machine's own CPU/GPU/RAM to run the model, so it is not
  "free" in an absolute sense — there's no external API bill, but local compute time and
  resources are the cost instead.

Which one is used is controlled entirely by the `Ai:Provider` setting — no code changes needed
to switch. **`Ai:Provider` defaults to `Ollama` in `appsettings.Development.json`**, so a fresh
`dotnet run` in Development already expects Ollama to be running.

## 1. Install Ollama

Not installed automatically — run one of these yourself:

- Windows/macOS: download the installer from https://ollama.com/download
- Linux: `curl -fsSL https://ollama.com/install.sh | sh`

## 2. Start Ollama

The installer normally registers Ollama as a background service that starts automatically. If
it isn't running:

```
ollama serve
```

By default it listens on `http://localhost:11434`.

## 3. Pull the configured model

AI-REAP is configured (see `Ai:Ollama:Model` below) to use **`llama3.1:8b`** by default — a
good balance for this app's workload (structured JSON extraction, requirements/architecture
reasoning, moderate document sizes) against what's practical to run on a typical development
laptop (~4.7 GB download, runs on 8 GB+ RAM; a GPU helps but isn't required).

```
ollama pull llama3.1:8b
```

If your machine has more RAM/a GPU and you want stronger output quality, `qwen2.5:14b` or
`llama3.1:70b` are reasonable upgrades — just change `Ai:Ollama:Model` to match (see below).

## 4. Configure AI-REAP to use Ollama

Already the Development default (`src/AiReap.Api/appsettings.Development.json`):

```json
"Ai": {
  "Provider": "Ollama"
}
```

The Ollama connection settings live in the base `src/AiReap.Api/appsettings.json`:

```json
"Ai": {
  "Provider": "Anthropic",
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "Model": "llama3.1:8b",
    "NumCtx": 8192,
    "TimeoutSeconds": 120
  }
}
```

- `BaseUrl` — where Ollama is listening. Only needs to change if Ollama runs on another host/port.
- `Model` — must exactly match a model you've `ollama pull`ed (check with `ollama list`).
- `NumCtx` — the model's context window in tokens. Ollama's own default is much smaller (often
  2048) than this app needs for larger uploaded requirement documents; 8192 gives real headroom.
  Raise it further if you see truncated-response errors on large inputs, at the cost of more RAM.
- `TimeoutSeconds` — the first request after Ollama loads a model can be slow; 120s gives it room.

None of these are secrets, so they live in the base `appsettings.json` (like
`Ai:Anthropic:Model`/`MaxTokens` already do) rather than the Development-only file.

## 5. Start the AI-REAP backend

```
dotnet run --project src/AiReap.Api
```

In the Development environment this now resolves `IAiChatClient` to `OllamaAiChatClient`
automatically — no other change needed. Check `GET /api/ai/status` (requires an authenticated
request, same as other API endpoints) to confirm the provider, reachability, and whether the
configured model is actually installed:

```json
{
  "provider": "Ollama",
  "configured": true,
  "modelAvailable": true,
  "model": "llama3.1:8b",
  "detail": null
}
```

If Ollama isn't running or the model isn't pulled, `modelAvailable` is `false` and `detail`
explains why — the same actionable message you'd get from a failed AI call.

## 6. Start the frontend

```
cd web
npm install   # first time only
npm run dev
```

The frontend has no provider-specific code — it always talks to the backend's normal API, and
the backend decides which provider actually answers.

## 7. Test Copilot / Requirement Analysis

Use the app exactly as with Anthropic: upload a requirement source, run Analyze, ask the
Project Copilot a question, generate requirements/user stories/etc. Every call is now served by
your local Ollama model instead of Anthropic. Responses may be slower and lower quality than
Anthropic's hosted models — that's expected for a free local model on development hardware.

If something fails, the error message should say why: Ollama not running, the model not
installed, a timed-out request, a truncated/malformed response, etc. — see
`OllamaAiChatClient.CompleteAsync` for the exact conditions.

## 8. Switch back to Anthropic

Two options, in order of preference:

- **Per-developer, without touching checked-in config:** set an environment variable or
  `dotnet user-secrets` value that overrides the Development default:
  ```
  dotnet user-secrets set "Ai:Provider" "Anthropic" --project src/AiReap.Api
  dotnet user-secrets set "Ai:Anthropic:ApiKey" "sk-ant-..." --project src/AiReap.Api
  ```
- **Team-wide default change:** edit `Ai:Provider` in `appsettings.Development.json` back to
  `"Anthropic"`.

Production/Staging config is untouched by any of this — the base `appsettings.json` still
defaults `Ai:Provider` to `Anthropic`, and a real deployment supplies
`Ai:Anthropic:ApiKey` the same way it always did.
