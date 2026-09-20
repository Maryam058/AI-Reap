# ADR-003 — Agent pipeline (PDF §36)

Status: Accepted (2026-09-19)

## Context

Phase 4 asks for agents (Requirements → Analysis → Architecture → Development Planning → QA → Review) with defined responsibilities, structured I/O, and human approval between stages — and explicitly no autonomous deployment (REAP-090..092).

Phases 1–3 already expose every capability an agent needs as a service (`IRequirementAnalysisService`, `IUserStoryService`, `ISolutionDesignService`, …), all writing to the generalized artifact model (ADR-002).

## Decisions

**1. Agents wrap existing services; they don't reimplement them.** An agent is an `IAgent` that calls the relevant Phase 1–3 services and returns an `AgentOutput`. No prompt is duplicated. The one exception is the Review Agent, whose narrative prompt sits on top of gap counts computed deterministically from the artifact graph — the model can't invent or hide a gap.

**2. The hand-off between agents is the database, not a message bus.** Agents never call each other. Each reads the artifacts earlier stages persisted. That keeps every intermediate result reviewable, editable and traceable through the existing artifact/version/relationship model, and means a human's edits between stages are picked up automatically.

**3. Orchestration state is two tables: `AgentRun` and `AgentStageRun`.** Not artifacts: a run isn't reviewed/versioned content, it is workflow state. `AgentStageRun.OutputJson` stores the structured `AgentOutput` only — never provider reasoning (same rule as `AIExecution`, REAP-010). The underlying services still write their own `AIExecution` rows, so the audit trail is unchanged.

**4. The human checkpoint is structural.** `AgentOrchestrator.ExecuteStageAsync` is the only place an agent runs, and it always ends in `AwaitingApproval` or `Failed`. The only code path that starts the next stage is `DecideAsync(approve: true)`. Each agent declares its own approver roles; the check is in the orchestrator, not just on the controller, so no other caller can bypass it. Reject halts the run; a `Failed` stage can be retried or abandoned.

**3a. What a stage approval means.** It is a decision to *proceed*. It does not approve the artifacts the stage generated; those keep the per-artifact workflow (`AiGenerated → Approved/Rejected`) and the Review Agent reports what is still unreviewed.

**5. Agents are idempotent per sub-step.** Each sub-step is skipped when its artifacts already exist (FRs, stories, tasks, per-story criteria, per-FR test cases). This makes retry-after-partial-failure and a later run against the same source safe, at the cost that regeneration isn't possible without deleting artifacts (no such endpoint yet).

**6. One active run per requirement source.** A run that is Running, AwaitingApproval or Failed blocks a new one (409), so two runs can't interleave writes to the same artifact set.

**7. REAP-092 is enforced by absence.** There is no deploy/release agent, no code generation, and nothing executes generated output. `AgentKind` has six values and adding a seventh that deploys would be a visible, reviewable change.

## Consequences / trade-offs

- Stages run synchronously in the request, like the other generators. Simple, and consistent with the rest of the API, but a slow real-model Analysis stage holds the request open. A background worker would need scoped-service and current-user plumbing that nothing else in the app needs yet.
- A crash mid-stage leaves the stage `Running` with no automatic recovery.
- Reject is terminal for a run; users start a new run, which reuses existing artifacts.
