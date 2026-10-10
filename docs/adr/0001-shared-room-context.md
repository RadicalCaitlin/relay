# ADR 0001: Shared room context

- **Status:** Accepted
- **Date:** 2026-10-10
- **Issue:** #6

## Context

Until now an agent saw only the messages addressed to it. Each agent kept its own conversation
through its CLI's resume mechanism, and nothing said to another agent, or to the room, ever reached
it. That is not one conversation; it is several private ones side by side.

Relay drives agents through their CLIs, one process per turn, with the prompt on standard input.
Any way of sharing context has to work within that: there is no long-lived connection to push
messages into, and the two CLIs share no feature for injecting history.

## Decision

**When the user addresses an agent, Relay first shows it what was said in the room that it has not
seen, then the user's message.**

### Visibility

- An agent is shown every message from the user, including messages addressed to another agent or
  to nobody, and every reply from every agent.
- Relay's own notices (failures, resets, unknown agents) are not shown. They are about Relay, not
  part of the conversation.
- There are no private messages. Anything said in a room is visible to every agent in it.
- A message the user addressed to a name that is not in the room (a typo such as `@cluade`) is
  shown as addressed to that name, never as a message to everyone.

### Delivery

- Each room records, per agent, which of its messages that agent has seen.
- The catch-up is plain text at the start of the prompt. It needs no CLI flag and behaves the same
  for every provider. The agent's own resumed conversation holds everything from earlier turns, so
  only what is new is sent.
- When there is nothing to catch up on, the agent receives the user's message exactly as typed.
- A completed turn marks as seen exactly the messages that turn showed the agent, plus its own
  reply. Anything said while the turn was in flight stays unseen.
- **An agent that will not remember the turn** (its session reports that the next message cannot
  continue the conversation, for example because the CLI returned no usable session id) has nothing
  marked as seen. It is shown the room afresh every turn, including its own earlier replies.

### Turns that do not complete

After a failure or a cancellation Relay cannot know whether the agent read what it was sent.

- If the failure happened before the agent could have received anything (the CLI did not start),
  nothing changes: the same messages are sent next time.
- Otherwise the messages are recorded as sent once. They are sent again on the next turn, and after
  a second turn that does not complete they are withheld: the agent is told how many messages were
  "already sent to you in an earlier turn that did not complete" instead of being sent them again.

So a message reaches an agent at most twice without a completed turn. This bounds how much a run of
failures can add to the agent's conversation. The cost is that an agent whose CLI failed twice
without recording the prompt is told about those messages rather than shown them.

### Starting again

- `/reset <agent>` is a clean slate: the agent forgets its conversation and is shown nothing said
  before the reset, only what is said afterwards.
- `/restart <agent>` starts a new conversation with the agent and shows it the room's recent
  messages again, including its own. This is how a broken conversation is recovered without
  retyping what was said.

### Attribution

The catch-up is a block with one line per message:

```
<room-transcript-3f9c1e0a7b24>
<message from="User" kind="human" to="Codex">@codex what is the plan?</message>
<message from="Codex" kind="agent" to="User">Use a queue.</message>
<message from="User" kind="human" to="everyone">the deadline is Friday</message>
</room-transcript-3f9c1e0a7b24>
```

- `from` and `to` are display names; `to` is `everyone` for a message addressed to nobody.
- `kind` says whether the author is the human or an agent, independently of the name.
- In message text `&`, `<`, `>` and `"` are escaped, and line breaks are written as `&#10;` and
  `&#13;`. Text can therefore neither close its own element nor open another, and cannot put a line
  of its own into the transcript. Every line inside the block is written by Relay.
- The block's name ends in a random value chosen for each prompt. The introduction tells the agent
  that only the text after that exact closing line is the user's message. Message text cannot
  contain the closing line, and cannot guess it either.
- No agent may take the ids `you` or `relay`, the names `User` or `everyone`, or another agent's
  name, so nothing can pass for the human, for Relay, or for someone else.

### Budget

- At most 20 messages are replayed in one turn.
- At most 16,000 characters of message text are replayed in one turn, and each message is cut at
  4,000 characters with a visible marker. Both are measured on the text as it is sent, after
  escaping, and a cut never falls inside an escaped character. The labels around each message are
  not counted; they add at most a line's worth each.
- The newest messages are kept. Older ones are replaced by a single line stating how many were left
  out, so the agent knows its view is incomplete.
- Characters stand in for tokens, which would need a tokenizer per provider. The values are the
  defaults of `ContextBudget`, which rejects values that cannot work. Making them configurable
  belongs with #13.

### Isolation between rooms

- What an agent has seen is held by the room and can only refer to that room's messages.
- An agent participant can belong to one room only. Adding it to a second throws, because its
  session carries what it was told and would take one room's conversation into another. The check
  is atomic, so two rooms cannot both add the same agent at the same moment.
- A room refuses to answer for an agent that is not one of its participants.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Replay the whole room history every turn | Grows without bound and repeats what the agent's own conversation already holds. |
| Put the transcript in a system prompt (`--append-system-prompt`) | Claude only; Codex has no equivalent, and a resumed Claude conversation reuses its recorded system prompt. |
| One shared conversation for all agents | Needs a provider-neutral session that neither CLI offers. |
| Share only agent replies, not what the user said to others | A reply without its question is often meaningless. |
| Private messages to a single agent | Deferred. More ways to be wrong about who saw what, and no demand yet. |
| Count a failed turn as seen | An agent could then silently miss messages. |
| Resend after every failed turn, without limit | A run of failures would put a copy of the transcript into the agent's conversation each time. |
| A single "seen up to here" position per agent | Marks as seen anything said while a turn was in flight, and cannot express "sent but not confirmed". |
| Have `/reset` also replay the room | Then there is no way to get a clean slate when something in the room is the problem. `/restart` covers recovery instead. |

## Consequences

- **Agents can now influence each other through the room.** One agent's reply becomes text another
  agent reads. Whatever steered the first (a file it read, a web page) can try to steer the second.
  The transcript is labelled as context and cannot be forged structurally, which lowers the risk
  but does not remove it: a model may still act on what it reads. Agents still never trigger each
  other; only a message from the user starts a turn. See `docs/threat-model.md`.
- **Nothing in a room is private from its agents**, including what you said to a different agent.
- **A long gap costs tokens.** An agent addressed after a long exchange between others receives up
  to the budget in one go.
- **Catch-up is lossy past the budget.** An agent that has been away for more than 20 messages
  sees only the latest, with a count of the rest.
- **A message can reach an agent twice**, after a turn that did not complete. After two such turns
  the agent is told about the messages rather than shown them.
- **Room history still stores only what was said**, not the prompts built from it.
- This is the base for agent-to-agent routing (#7) and turn-taking (#8), which will need to decide
  whether an agent's reply can address another agent.
