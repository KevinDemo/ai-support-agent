// Builds the static demo into docs-site/. Needs only Node. Run from anywhere:
//   node static-demo/build.mjs
import { cpSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { page, startOutput } from "./kit/shell.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const out = join(root, "docs-site");
const wwwroot = join(root, "src", "Harbour.SupportAgent", "wwwroot");
startOutput(out, wwwroot);

for (const file of ["chat.js", "engine.mjs"]) cpSync(join(here, "src", file), join(out, file));
cpSync(join(wwwroot, "site.css"), join(out, "site.css"));

const scenarioDir = join(root, "evals", "Harbour.SupportAgent.Evals", "scenarios");
const scenarios = ["01-normal.json", "02-guardrails.json", "03-scope-and-handoff.json"]
  .flatMap((f) => JSON.parse(readFileSync(join(scenarioDir, f), "utf8")))
  .map(({ name, why, script, turns }) => ({ name, why, script, turns }));
writeFileSync(join(out, "scenarios.json"), JSON.stringify(scenarios));

const summary = readFileSync(join(root, "evals", "results", "latest.md"), "utf8").split(/\r?\n/).find((l) => /passed, \d+ failed/.test(l));

const body = `
<main class="layout static">
  <aside class="brief">
    <h1>Where is my parcel?</h1>
    <p>A support agent whose guardrails are written in code, so it cannot share details before an identity check or change a delivery without a clear yes. This runs the service's offline keyword model, not a language model, so unusual wording may not be understood.</p>
    <p class="model">Model: <span id="model"></span></p>

    <h2>Demo consignments</h2>
    <table>
      <thead><tr><th>Number</th><th>Postcode</th><th>State</th></tr></thead>
      <tbody>
        <tr><td class="mono">HF1000001</td><td class="mono">2998</td><td>In transit</td></tr>
        <tr><td class="mono">HF1000002</td><td class="mono">4997</td><td>Delivered</td></tr>
        <tr><td class="mono">HF1000003</td><td class="mono">3996</td><td>Out for delivery</td></tr>
        <tr><td class="mono">HF1000004</td><td class="mono">2998</td><td>Delayed</td></tr>
      </tbody>
    </table>

    <h2>Try saying</h2>
    <ul id="suggestions" class="suggestions"></ul>

    <h2>Replay an eval scenario</h2>
    <p class="hint">The repo's evals, including a model scripted to misbehave so the gateway has to stop it. Last run: ${summary}</p>
    <ul id="scenarios" class="suggestions"></ul>
  </aside>

  <section class="chat" aria-label="Support chat">
    <p id="replay-note" class="replay-note" hidden></p>
    <div id="messages" class="messages" role="log" aria-live="polite"></div>
    <div id="pending" class="pending" hidden>
      <p id="pending-summary"></p>
      <button type="button" id="confirm">Yes, go ahead</button>
      <button type="button" id="cancel" class="ghost">No, cancel</button>
    </div>
    <form id="composer" class="composer">
      <label for="text" class="sr">Your message</label>
      <input id="text" autocomplete="off" maxlength="2000" placeholder="Type your message" required>
      <button type="submit">Send</button>
      <button type="button" id="restart" class="ghost">Start over</button>
    </form>
  </section>

  <section class="trace-panel" aria-labelledby="trace-title">
    <h2 id="trace-title">What the agent did</h2>
    <p class="hint">Every tool call and guard, as staff see it in the service's conversation log.</p>
    <p id="trace-empty" class="hint">Nothing yet. Tool calls appear here as the conversation runs.</p>
    <ol id="trace" class="events"></ol>
  </section>
</main>
`;

writeFileSync(join(out, "index.html"), page({
  repo: "ai-support-agent",
  product: "AI support agent",
  title: "Parcel help",
  lead: "A courier support agent with guardrails in code, running in your browser.",
  body,
  styles: ["site.css"],
  scripts: ["chat.js"],
}));
console.log("built docs-site/");
