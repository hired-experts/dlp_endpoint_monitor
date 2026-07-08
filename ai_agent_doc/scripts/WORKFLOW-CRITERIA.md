# Workflow criteria and harness (DlpEndpointMonitor)

How substantive work is executed in this repo: as a **Workflow-tool script** under
`ai_agent_doc/scripts/`, not as ad-hoc solo edits. This file is the contract for authoring
a new one. Copy `ai_agent_doc/scripts/_base.workflow.js` and adapt it.

## What a workflow is here

- One file per task/phase: `ai_agent_doc/scripts/<task>.workflow.js`, run via the Workflow
  tool (`Workflow({ scriptPath: "ai_agent_doc/scripts/<task>.workflow.js" })`).
- Plain JS (not TS). Begins with a pure-literal `export const meta = {...}` (name,
  description, phases), then the body uses `agent()` / `parallel()` / `pipeline()` /
  `phase()` / `log()`.
- Phase artifacts (handoff files) live under `ai_agent_doc/scripts/.<task>/`. Both
  `ai_agent_doc/scripts/*.workflow.js` and `ai_agent_doc/scripts/.<task>/` are
  **git-ignored** local orchestration; only the tracked script is this file plus
  `_base.workflow.js`.
- Results: the script's `return` value comes back in the completion notification;
  per-agent returns are in the run's `journal.jsonl`.

## The two shapes (pick the lean one unless you must edit)

| | A. Audit / read-only (LEAN) | B. Build-and-verify (HEAVY) |
|---|---|---|
| Structure | fan-out finders -> one adversarial verify | worker->verifier loop per phase, capped |
| Fan-out | `parallel(DIMENSIONS.map(...))` | sequential phases, handoff files between |
| Edits files? | no (reports only) | yes (scoped to named files + handoffs) |
| Use when | find/verify/measure, no mutation | implement/fix/complete with verification |

Default to **A**. Reach for **B** only when the task mutates files and needs an
iterate-until-verified loop. B is roughly 4x the cost of A; scope it tightly.

This is a single-project, no-test, no-CI C# native binary - most useful workflows here are
shape A: audit the block/restore escalation ladder for a new device class, sweep every
`Actions/*` P/Invoke call for an unchecked failure path, verify a new command/event was
wired through `CommandsJsonContext`/`AppJsonContext` correctly, or check that a change to
`DeviceKindResolver` doesn't silently reclassify an existing GUID. Reach for shape B only
for genuine multi-file feature work (e.g. adding a new monitored device class end to end:
a new `Monitors/*` + `Actions/*` pair + command/event wiring + schema regeneration).

## Mandatory harness elements (both shapes)

1. **Anti-drift harness string**, injected into every agent prompt: code is ground truth
   (open the real file before claiming anything); explicit edit scope (name the files an
   agent may touch); no invention of commands/events/enum values/instance-ID formats; preserve
   what is already correct - especially the safety gates (`IsProtectedInternal`,
   `HasBluetoothAncestor`) and the fail-safe defaults documented in PROJECT.md section 8.
2. **Structured output** via a JSON `schema` on `agent()` for anything post-processed -
   never parse prose. Validation happens at the tool layer, so the model retries on
   mismatch.
3. **Adversarial verify**: a separate, skeptical agent (or panel) re-checks candidate
   results and defaults to reject/false-positive when unsure. Report only what survives.
4. **Model routing** (cost control): a cheap model for finders/grunt work, a stronger model
   for edits, the strongest for the verify/judge pass. Set per-`agent()` via `model`; use
   `effort: 'low'` for cheap mechanical stages.
5. **Caps and scope guards**: cap any loop (`MAX_ITERS`); scope git-diff checks to the
   files the workflow may touch (`git diff --stat -- <paths>`) so an in-progress branch
   does not trip them; `log()` anything intentionally dropped (top-N, no-retry) so silent
   truncation never reads as full coverage.

## Token-precision rules

- Prefer read-only + fan-out over edit + loop. A barrier/loop is only justified when a
  stage genuinely needs all prior results together.
- Bound the fan-out to a fixed `DIMENSIONS`/work-list; do not spawn open-endedly.
- Cheapest model that can do the stage; reserve the strongest model for the single
  verify/judge step.
- Structured schemas keep agent replies short and machine-checkable (cheaper than prose).
- Give finders an exclusion list (`bin/`, `obj/`, `.vs/`, `ai_agent_doc/scripts/`) in the
  prompt - this repo has no `node_modules`, but the `.NET` build output directories are
  the equivalent noise to exclude.

## Running and iterating

- Run: `Workflow({ scriptPath: "ai_agent_doc/scripts/<task>.workflow.js" })` (runs in
  background; you are notified on completion). Watch live with `/workflows`.
- Iterate: edit the file, re-invoke with the same `scriptPath`; add
  `resumeFromRunId: "<runId>"` to replay unchanged agents from cache and only re-run edits.
- `Date.now()` / `Math.random()` / `new Date()` are unavailable in scripts (they break
  resume). Pass timestamps via `args`; vary randomness by agent index.

## Author checklist for a new workflow

1. Copy `ai_agent_doc/scripts/_base.workflow.js` to `ai_agent_doc/scripts/<task>.workflow.js`.
2. Fill `meta` (pure literal; phase titles match the `phase()` calls).
3. Write the CRITERIA/spec constant. The anti-drift HARNESS is inherited from the base
   UNCHANGED (GROUND TRUTH, REUSE FIRST, NO INVENTION, PRESERVE are permanent baseline
   rules) - the only per-task edit is filling the SCOPE line's read-only/edit placeholder
   with THIS task's files. REUSE FIRST is mandatory: reuse existing code at every layer
   (see the base HARNESS) - never hand-roll a raw P/Invoke call, a new device-kind GUID
   mapping, or a duplicate JSON record shape when the project already has a helper for it
   (`Actions/*` for Win32 calls, `DeviceKindResolver`/`GetKindFromCoD` for classification,
   `Core/UsbDeviceList` for any new persisted device list).
4. Define the output `schema`(s).
5. Choose shape A or B; set `model` per stage.
6. Keep the fan-out bounded; cap any loop; scope the diff check.
7. `return` a structured summary (counts + the surviving findings/changes).
