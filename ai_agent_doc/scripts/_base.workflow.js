// BASE WORKFLOW TEMPLATE - copy to ai_agent_doc/scripts/<task>.workflow.js and adapt.
// Read ai_agent_doc/scripts/WORKFLOW-CRITERIA.md first. This repo is a single C#/.NET
// project (DlpEndpointMonitor/DlpEndpointMonitor.csproj) - no npm workspaces, no test
// project, no CI. Adjust file globs/paths accordingly when adapting the HARNESS below.
//
// meta MUST be a pure literal (no variables/spreads/calls). Phase titles here must match
// the phase() / opts.phase strings used below.
export const meta = {
  name: 'base-template',
  description: 'One-line description shown in the permission dialog and workflow list.',
  whenToUse: 'When to reach for this workflow.',
  phases: [
    { title: 'Find' },
    { title: 'Verify' },
  ],
}

// -- Spec / criteria for THIS task (what "correct" means). Injected into every prompt. --
const CRITERIA = `
CRITERIA: state the exact rule(s) this workflow checks or enforces, grounded in AGENTS.md /
PROJECT.md (both in ai_agent_doc/). Be concrete enough that a finding is objective.
Scope: list the files/folders in play (e.g. Monitors/, Actions/, Core/). Exclude bin/, obj/,
.vs/, ai_agent_doc/scripts/.`

// -- Anti-drift harness. Injected into every agent prompt (both shapes). ------------------
const HARNESS = `
=== ANTI-DRIFT HARNESS - obey on every action ===
1. GROUND TRUTH: the code is the source of truth, not your memory. Open the real file and
   confirm before writing any claim about a command/event/enum value/instance-ID format/
   registry key.
2. REUSE FIRST (any existing code, not just utils/helpers): before writing ANY code, search
   the codebase for something that already does the job and USE it instead of hand-rolling.
   Applies to every layer, e.g.: Win32 calls -> the existing Actions/* wrappers (UsbActions,
   BluetoothActions, DisplayActions, ClipboardActions), NEVER a raw P/Invoke duplicated
   inline in a Monitor or Handler; device classification -> DeviceKindResolver
   (Core/UsbKind.cs) / BluetoothActions.GetKindFromCoD, never a new ad-hoc GUID/CoD table;
   persisted device lists -> Core/UsbDeviceList (extend, do not reimplement the
   ReaderWriterLockSlim + atomic-temp-file-write pattern); wire shapes -> a new
   ICommand/IEvent record with [JsonDiscriminant]/[EmitsEvent], registered in
   CommandsJsonContext/AppJsonContext, never a hand-rolled JsonSerializer.Serialize call
   outside EventEmitter.Emit; stdout -> EventEmitter.Emit exclusively, never a raw
   Console.WriteLine. Rule of thumb: grep for an existing symbol before writing raw code;
   if nothing fits and a new shared helper is genuinely warranted, add ONE small helper in
   the right layer (Actions/ for Win32, Core/ for state) rather than repeating a raw pattern.
3. SCOPE: <read-only: "Do NOT edit any file." | edit: "You may edit ONLY <files> and
   handoff files under ai_agent_doc/scripts/.<task>/.">. This is a live branch - leave
   unrelated changes untouched.
4. NO INVENTION: never invent commands, events, DeviceKind values, registry keys, or file
   paths. Keep existing terminology (the exact wire strings in Core/Enums.cs).
5. PRESERVE: do not rewrite anything already correct - especially the safety gates
   (UsbActions.IsProtectedInternal, HasBluetoothAncestor) and fail-safe defaults
   documented in PROJECT.md section 8 ("Fail Fast, Fail Safe"). Never loosen a safety
   check to make a test/finding pass.
STYLE: prefer guard-clause early returns over nested if/else; Win32-facing functions return
(bool ok, string? error) tuples, not exceptions, for expected failure modes; write direct,
objective comments that explain WHY not what (self-documenting code) - a line or two, never
narrate the next line. Nullable reference types are enabled - never silence a nullable
warning with '!' without proving non-null at that point.
=== END HARNESS ===`

// -- Structured output. Post-processed agent results MUST use a schema (never parse prose).
const FIND_SCHEMA = {
  type: 'object', additionalProperties: false, required: ['findings'],
  properties: { findings: { type: 'array', items: {
    type: 'object', additionalProperties: false, required: ['file', 'line', 'why', 'severity'],
    properties: {
      file: { type: 'string' }, line: { type: 'number' },
      why: { type: 'string' }, severity: { type: 'string', enum: ['high', 'medium', 'low'] },
    } } } },
}
const VERIFY_SCHEMA = {
  type: 'object', additionalProperties: false, required: ['confirmed'],
  properties: { confirmed: { type: 'array', items: {
    type: 'object', additionalProperties: false, required: ['file', 'line', 'why', 'severity', 'verdict'],
    properties: {
      file: { type: 'string' }, line: { type: 'number' }, why: { type: 'string' },
      severity: { type: 'string', enum: ['high', 'medium', 'low'] },
      verdict: { type: 'string', enum: ['CONFIRMED', 'FALSE_POSITIVE'] }, note: { type: 'string' },
    } } } },
}

// ===========================================================================================
// SHAPE A (DEFAULT - lean, read-only): bounded fan-out finders -> one adversarial verify.
// ===========================================================================================
const DIMENSIONS = [
  { key: 'dimension-1', prompt: 'Focus area 1: what to look for and where (e.g. Monitors/).' },
  { key: 'dimension-2', prompt: 'Focus area 2: what to look for and where (e.g. Actions/).' },
]

phase('Find')
const found = await parallel(DIMENSIONS.map((d) => () =>
  agent(`Read-only audit (NO edits). ${CRITERIA}\n\nFOCUS: ${d.prompt}\n\nOpen the real files; report file:line-accurate findings.`,
    { label: `find:${d.key}`, phase: 'Find', effort: 'low', schema: FIND_SCHEMA })
    .then((r) => (r?.findings ?? []).map((f) => ({ ...f, dimension: d.key })))
))
const candidates = found.filter(Boolean).flat()
log(`raw candidates: ${candidates.length}`)

phase('Verify')
const verdict = await agent(
  `You are an adversarial verifier. ${CRITERIA}\n\nOpen the ACTUAL files and verify each candidate. Be strict:
   default to FALSE_POSITIVE if the criterion is actually satisfied. Return every candidate with a verdict
   + short note, most-severe CONFIRMED first.\n\nCANDIDATES:\n${JSON.stringify(candidates, null, 1)}`,
  { label: 'verify', phase: 'Verify', schema: VERIFY_SCHEMA }
)
const confirmed = (verdict?.confirmed ?? []).filter((f) => f.verdict === 'CONFIRMED')
return { totalCandidates: candidates.length, confirmedCount: confirmed.length, confirmed }

// ===========================================================================================
// SHAPE B (HEAVY - edits + iterate-until-verified). Uncomment and replace the SHAPE A body.
// Sequential phases, on-disk handoffs, capped worker->verifier loop. ~4x the cost of A.
// ===========================================================================================
// const WORKDIR = 'ai_agent_doc/scripts/.base-template'   // handoff dir
// const MAX_ITERS = 17
// const VERDICT_SCHEMA = { type: 'object', additionalProperties: false,
//   required: ['complete', 'remaining', 'notes'], properties: {
//     complete: { type: 'boolean' }, remaining: { type: 'array', items: { type: 'string' } },
//     notes: { type: 'string' } } }
//
// async function runTask(title, workerPrompt, verifierPrompt) {
//   let v = { complete: false, remaining: ['not started'], notes: '' }, iter = 0
//   while (!v.complete && iter < MAX_ITERS) {
//     iter++
//     await agent(`${HARNESS}\nIteration ${iter}/${MAX_ITERS} of "${title}".\n${workerPrompt}\n` +
//       `If already done, make no change and say so.`,
//       { label: `${title}:worker#${iter}`, phase: title })
//     v = await agent(`${HARNESS}\nIndependent verifier for "${title}". Be skeptical; default complete=false.\n${verifierPrompt}`,
//       { label: `${title}:verify#${iter}`, phase: title, schema: VERDICT_SCHEMA })
//     log(`${title} iter ${iter}/${MAX_ITERS} - complete=${v.complete} - remaining=${v.remaining.length}`)
//   }
//   if (!v.complete) log(`WARNING: ${title} hit the ${MAX_ITERS}-iter cap. Remaining: ${v.remaining.join('; ')}`)
//   return { complete: v.complete, iters: iter, verdict: v }
// }
//
// phase('Implement')
// const t1 = await runTask('Implement',
//   `Do the next unit of work by editing ONLY the in-scope files. Confirm against code first.`,
//   `Is the work complete and code-backed? List anything unresolved in "remaining".`)
// // Scope the final diff check to your files so the live branch does not trip it:
// //   git diff --stat -- <file-a> <file-b>
// return { complete: t1.complete, iters: t1.iters, remaining: t1.verdict.remaining }
