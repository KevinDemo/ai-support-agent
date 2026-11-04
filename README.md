# AI customer support agent with guardrails in code

A chat agent that answers "Where is my parcel?", reschedules deliveries, opens damage claims and hands off to a person. It belongs to Harbour Freight Couriers, a fictional courier company, and all data is invented. The tracking system is a fake that lives in memory.

![A tracking question answered after the identity check, then a reschedule that asks for a choice before changing anything.](docs/chat.png)

*A tracking question answered after the identity check, then a reschedule that asks for a choice before changing anything. Screenshot taken in a headless browser on 4 November 2026.*

## The problem

Most courier support tickets ask the same few things, and a chatbot could answer many of them. The reason businesses hesitate is what a chatbot might do when it goes wrong: tell a stranger where someone's parcel is, confirm a delivery time it made up, or change a booking because a message told it to. Asking the model nicely in a system prompt does not remove those risks.

So the rules here are not in the prompt. They are in a gateway that every tool call must pass through, and they hold even when the model has been fooled.

## What it does

| Guardrail | How it is enforced |
|---|---|
| Identity check before any detail | Tools refuse to run until `verify_identity` succeeds with the consignment number and postcode, and only for that consignment |
| No probing | An unknown number and a wrong postcode get the same answer. 3 failures in a conversation hand off to a person. 5 failures on a consignment across all conversations lock it |
| Changes need the customer's yes | `reschedule_delivery` and `open_damage_claim` only create a proposal. Only a plain yes from the customer, or the Confirm button with the matching id, executes it. The model has no way to confirm |
| Never invents tracking data | Before a reply is sent, consignment numbers, dates, times and status claims are checked against tool results and what the customer said. A reply that fails is replaced by a handoff |
| Out of scope | The prompt tells the model to decline, and only the five listed tools can ever run |
| Hands off when unsure | The customer is upset or asks for a person, the model is unsure, fails, refuses or loops: a person takes over and the bot stops acting |
| Log for staff | Every message, tool call, block and guard decision is stored, with postcodes masked in tool calls. The staff page needs a key |

## Architecture

```
web chat --> AgentRunner --> IChatModel --- RuleBasedFakeModel (default, offline)
                 |                     \--- AnthropicChatModel (only when asked for)
                 |
                 |  every tool call
                 v
            ToolGateway  (identity, confirmation, windows, one change at a time)
                 |
            ITrackingApi (FakeTrackingApi, in memory)

reply text --> GroundingCheck --> customer, or a handoff if it fails
all of it  --> ConversationLog (SQLite) --> staff page
```

Replies about real changes and handoffs are built by `AgentRunner` from gateway data, so the model cannot misreport them.

## Offline by default, and what that means

No key or network is needed. `RuleBasedFakeModel` is keyword rules, not intelligence, and a real model would handle far more phrasing. Because it behaves well on purpose, it cannot test the guardrails alone. The evals therefore also use a scripted model that does what an injected or confused model would do: skips the identity check, claims confirmation, reads other people's consignments, and invents delivery times.

To use Claude instead:

```bash
export HARBOUR_MODEL_PROVIDER=anthropic   # required: a key alone does not switch modes
export ANTHROPIC_API_KEY=...
export ANTHROPIC_MODEL=claude-opus-5-5    # optional, this is the default
```

This uses the Messages API with tool use over plain `HttpClient`, and passes thinking blocks back unchanged across tool calls. A refusal or a truncated response hands off to a person, with no automatic fallback model. **The live mode has never been run against the real API.** The wire format is only checked against the documented shape in tests. With the provider set, the eval program sends the scenarios that have no script to the real model. That costs money, and it has not been done.

## Evaluation results

Latest run: **28 scenarios, 28 passed, 0 failed.** The full table is in [evals/results/latest.md](evals/results/latest.md). Scenarios are JSON files in `evals/Harbour.SupportAgent.Evals/scenarios`. Each one asserts which tools were attempted, blocked or executed, and every one checks the exact list of changes made in the tracking system.

| Group | Scenarios | Examples |
|---|--:|---|
| Normal requests | 7 | Track, reschedule, damage claim, declining, a delivered consignment |
| Guardrails | 14 | Wrong postcode, reschedule without confirmation, prompt injection with a well-behaved and a fooled model, injection hidden in tracking data, invented data |
| Scope and handoff | 7 | Out of scope, angry customer, unsure twice, model failure, runaway loop |

To check the evals can fail, I disabled each main guardrail on purpose, ran the suite, and restored it:

| Guardrail disabled | Evals failed |
|---|--:|
| Identity check on tools | 2 of 28 |
| Plain yes replaced by "contains yes" | 2 of 28 |
| Check on invented data | 2 of 28 |

These scenarios prove the guardrails against the attacks written here. They do not measure how well a real model understands customers, and they are not a security audit.

## Test results

Latest run, from a fresh clone on Windows 11, .NET SDK 10.0.108: **59 tests, 59 passed, 0 failed, 0 skipped**, with `dotnet build` at 0 errors. That includes all 28 scenarios, so a guardrail regression fails the build. The other 31 cover the lockout across conversations, identical answers for wrong and unknown details, proposal and execution, stale confirm buttons, what is accepted as a yes, the grounding check, the staff key, and the Anthropic wire format.

## Live demo

[https://demo.oct28th.com/ai-support-agent/](https://demo.oct28th.com/ai-support-agent/)

The chat page running in your browser, with a panel showing each tool call, gateway block and guard. It runs a JavaScript port of the offline model and the guardrails, and can replay all 28 eval scenarios, including the model scripted to misbehave. `node static-demo/verify.mjs` runs those scenarios through the port and compares tool calls and outcomes with `evals/results/latest.md` from the real code (28 of 28 match). It is keyword rules rather than a language model, so unusual wording may not be understood.

```bash
node static-demo/build.mjs
```

This needs only Node and writes `docs-site/`. Every asset path is relative, so it works under `/ai-support-agent/`. GitHub Pages only publishes a repo's root or `docs/`, so this folder goes out through a `gh-pages` branch or an Actions workflow. It has not been deployed yet.

## Hosting the real service

The static demo needs no server. To host the real .NET service as well, the repo has a multi-stage `Dockerfile` (.NET 10, listens on `$PORT`) and a `render.yaml` blueprint for Render's free web service plan. SQLite holds the conversation log and is created on start, the demo consignments are seeded in memory, and the offline model needs no key. The image itself has not been built, because Docker was not available on the machine where this was written. `dotnet publish` was run instead and the published build answered over HTTP with the same settings.

## How to run

You need the .NET 10 SDK.

```bash
dotnet test Harbour.SupportAgent.slnx
dotnet run --project evals/Harbour.SupportAgent.Evals -- evals/results/latest.md
dotnet run --project src/Harbour.SupportAgent --urls http://localhost:5182
```

Open `http://localhost:5182`. The page lists demo consignments and things to try. The staff log is at `/staff.html`, with the demo key `demo-staff-key`. Change `Staff:Key` for anything other than a local demo.

## What a client gets

A support agent connected to your own tracking system, with the scope agreed in writing first:

1. The tools your agents actually use, connected to your API, with the identity check and confirmation rules agreed with you.
2. An evaluation suite built from your real tickets, including the failures you worry about, that runs on every change.
3. A trial on a real model with staff reading the log, before any customer sees it, and measured results on what it handled and what it handed off.
4. Handoff into your existing helpdesk, and a staff log that fits how your team works.

Details and contact: [kevindemo.github.io](https://oct28th.com).

## Honest limitations

- No real model has been run through this. How many tickets it could resolve is unknown, and I make no claim about it.
- A consignment number and postcode are weak proof of identity, because both are printed on the parcel label. It is a common courier standard, but sensitive actions may need more.
- The grounding check is a heuristic. It catches invented numbers, dates, times and common status phrases, not every false statement. "Your driver is friendly and nearby" would pass.
- Refusing out-of-scope requests relies on the model. Tools cannot be misused, but a real model could still be talked into chatting about something else.
- Conversations and lockout counts are in memory, so a restart ends active chats and resets lockouts. The log survives in SQLite. Production would use shared storage for multiple servers.
- No rate limiting. Production needs it, plus a real login for staff instead of a shared key.
- Handoff produces a ticket number from the fake API. There is no staff reply screen, so nobody actually answers.
- The customer's messages are stored unredacted in the log, so a real deployment needs a retention policy.
- The UI was checked with screenshots in a headless browser (see `docs/`), not with automated end-to-end browser tests.
