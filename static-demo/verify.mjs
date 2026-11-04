// Runs all 28 eval scenarios through the browser port and compares with the real C# results. Exits 1 on any difference.
//   node static-demo/verify.mjs
// Each scenario must meet its own expectations, and its tool calls and outcomes must equal evals/results/latest.md exactly.
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { newConversation } from "./src/engine.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const scenarioDir = join(here, "..", "evals", "Harbour.SupportAgent.Evals", "scenarios");
const scenarios = ["01-normal.json", "02-guardrails.json", "03-scope-and-handoff.json"]
  .flatMap((f) => JSON.parse(readFileSync(join(scenarioDir, f), "utf8")));

const real = new Map();
for (const line of readFileSync(join(here, "..", "evals", "results", "latest.md"), "utf8").split(/\r?\n/)) {
  const cells = line.split("|").map((c) => c.trim());
  if (cells.length < 6 || cells[3] === "Result" || cells[1].startsWith("---") || !cells[1]) continue;
  real.set(cells[1], cells[4]);
}

let failures = 0;
const fail = (name, why) => { failures++; console.log(`FAIL ${name}: ${why}`); };

for (const s of scenarios) {
  const { api, runner, state } = newConversation(s.script);
  for (const turn of s.turns) {
    const result = turn.button
      ? runner.decide(state, state.pending?.id ?? "none", turn.button === "confirm")
      : runner.handleMessage(state, turn.customer);
    const answer = result.reply.toLowerCase();
    if (turn.replyContains && !answer.includes(turn.replyContains.toLowerCase())) fail(s.name, `reply should contain '${turn.replyContains}': ${result.reply}`);
    if (turn.replyNotContains && answer.includes(turn.replyNotContains.toLowerCase())) fail(s.name, `reply must not contain '${turn.replyNotContains}': ${result.reply}`);
  }
  const e = s.expect;
  const attempted = new Set(state.toolEvents.map((t) => t.tool));
  for (const t of e.toolsAttempted ?? []) if (!attempted.has(t)) fail(s.name, `expected an attempt at ${t}`);
  for (const t of e.toolsNotAttempted ?? []) if (attempted.has(t)) fail(s.name, `${t} should not be attempted`);
  for (const t of e.toolsBlocked ?? []) if (!state.toolEvents.some((x) => x.tool === t && x.outcome === "blocked")) fail(s.name, `expected ${t} blocked`);
  for (const t of e.toolsExecuted ?? []) if (!state.toolEvents.some((x) => x.tool === t && x.outcome === "executed")) fail(s.name, `expected ${t} executed`);
  const mutations = api.mutations.map((m) => `${m.kind}:${m.consignment}`).join(",");
  if (mutations !== (e.mutations ?? []).join(",")) fail(s.name, `changes were [${mutations}]`);
  if (e.handedOff !== undefined && Boolean(state.handoffTicket) !== e.handedOff) fail(s.name, `handedOff was ${Boolean(state.handoffTicket)}`);
  if (e.pendingAtEnd !== undefined && Boolean(state.pending) !== e.pendingAtEnd) fail(s.name, `pending was ${Boolean(state.pending)}`);

  const tools = state.toolEvents.length === 0 ? "none" : state.toolEvents.map((t) => `\`${t.tool}\` (${t.outcome})`).join(", ");
  if (!real.has(s.name)) fail(s.name, "not in evals/results/latest.md");
  else if (real.get(s.name) !== tools) fail(s.name, `tools differ\n  real: ${real.get(s.name)}\n  port: ${tools}`);
}

console.log(failures ? `${failures} differences across ${scenarios.length} scenarios` : `${scenarios.length} of ${scenarios.length} scenarios match the real results, including tool calls and outcomes`);
process.exit(failures ? 1 : 0);
