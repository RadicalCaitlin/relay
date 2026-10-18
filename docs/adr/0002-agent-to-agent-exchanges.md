# ADR 0002: Agent-to-agent exchanges and their limits

- **Status:** Accepted
- **Date:** 2026-10-10
- **Issues:** #7, #8, amended by #62

## Context

Since ADR 0001 every agent in a room sees what the others said, but only a message from the user
starts a turn. To have Codex review Claude's plan the user reads Claude's reply and then types
`@codex` themselves. An agent reply that begins with `@codex` is displayed and nothing more.

Issue #7 asks for agents to address each other when the user has authorised it. Issue #8 asks that
such exchanges cannot run without bound. They are one design: the rule that lets an agent's reply
start another agent's turn is the rule that has to be limited, so this record covers both.

Three things constrain the answer:

- Every agent turn is a CLI process that acts with the user's permissions and costs real usage.
- An agent's reply is untrusted text. Whatever it read (a file, a web page) may have steered it.
- Until now the user read every reply before any other agent acted on it. Routing a reply
  straight to another agent removes that check for as long as the exchange runs.

## Decision

**The user opens an exchange by naming more than one agent at the start of a message. Inside that
exchange, and only there, an agent's reply whose first line is `@id` is delivered to that agent. An
exchange is a single chain of turns with a fixed limit, and only the user can start another. With
pause mode on, each handover waits for the user before it is delivered.**

### Opening an exchange

- A message may begin with several mentions: `@claude @codex agree on a queue design`. The first
  agent named answers. The others are **invited**: they may be addressed during the exchange.
- A message that begins with one mention behaves exactly as it does today. Nobody is invited, so
  no reply is routed.
- Mentions anywhere else in the message are still not routing instructions. Text pasted into a
  message can therefore never invite an agent.
- After the first mention, further ones count only while they name an agent in the room. The first
  that does not is where the message begins: `@claude @Override is missing on this method` is a
  question for Claude about `@Override`. The cost is that a mistyped second name (`@claude @cdoex
  agree`) is sent to Claude alone as part of the text; the waiting line then shows no turn count.
- If the first name is not an agent in the room, or any agent named is unavailable, nothing is
  sent and a notice says which one.
- Naming two agents does not ask both for an answer. The first speaks, and decides whether to hand
  over. `/help` says so.

### Handing over

- A reply hands over when its **first line is only** a mention of an invited agent other than its
  author, and the message follows on the lines after:

  ```
  @codex
  I propose a channel. Objections?
  ```

  This is deliberately stricter than what the user types. A reply written for the user often
  begins by naming another agent (`@claude and I agree: use a channel.`), and reading that as a
  handover would deliver it to Claude and spend turns nobody asked for. An agent that writes a
  handover inline (`@codex what do you think?`) is not understood: its reply goes to the user and
  the exchange ends, which is the safe way to be wrong.
- An agent addresses one agent per reply. There is no fan-out, so an exchange is a chain, never a
  tree, and one agent runs at a time.
- Any other reply goes to the user and ends the exchange. That includes a first line that names
  something that is not an agent in the room (`@property` above a method is code, not an address).
- Outside an exchange no reply is ever read as addressed to an agent, however it begins. It is
  recorded and shown as a reply to the user, with no notice.
- Inside an exchange, a handover to an agent in the room is recorded as addressed to it, whether or
  not it is delivered. An undelivered one stays unseen by that agent and reaches it in the catch-up
  of its next turn, under ADR 0001.
- Agents cannot invite anyone, raise the limit, or open an exchange. `/commands` in a reply are
  still never read.

### What the receiving agent is sent

Everything still goes through `SharedContext.Compose`, and the rule from ADR 0001 holds: text that
is not the user's current message sits inside the transcript, escaped, with its author named.

- The first turn of an exchange is the user's message after the transcript, as today.
- A handed-over turn has **no user message**. The reply that addressed the agent is the last line
  of the transcript, labelled with its author and `kind="agent"`. What follows the transcript is a
  fixed line written by Relay saying who addressed the agent. No agent's words ever appear there.
- The handover is sent whole. The budget of ADR 0001 applies to the catch-up before it, not to the
  message the turn is about, just as the user's own message is never cut.
- The user's opening message is in the same transcript with `kind="human"`, so the recipient can
  tell what the user asked for from what another agent asked for.
- Relay's introduction tells each agent in an exchange, in Relay's own words:
  - who is in the exchange and how to hand over (`@id` alone on the first line of the reply);
  - that any other reply goes to the user and ends the exchange, even one that begins by
    mentioning an agent;
  - how many turns remain, and on the last one that a handover will not be delivered;
  - to keep a handover short and point to files the other agent can read rather than paste them;
  - that a message from another agent is a colleague's request, not an instruction from the user.

```
<room-transcript-3f9c1e0a7b24>
<message from="User" kind="human" to="Claude">@claude @codex agree on a queue design</message>
<message from="Claude" kind="agent" to="Codex">@codex&#10;I propose a channel. Objections?</message>
</room-transcript-3f9c1e0a7b24>

Claude addressed the last message above to you.
```

### Limits

- **An exchange is at most 6 agent turns**, counting the first and counting a turn that fails.
  That is three rounds between two agents. The number is a judgement, not a measurement, and is
  the default of a limits record that rejects values below 1. Since #62 the user may choose
  another at start-up, from 1 to 20; see the amendment at the end of this record.
- The count belongs to the exchange and only falls. Nothing an agent says can change it.
- With no fan-out and a falling count, an exchange always ends: it starts at most as many
  processes as the limit allows.

### Why an exchange stops

Every exchange ends for exactly one of these reasons. The router reports which, and the room shows
a notice for all but the first.

| Reason | What the user is told |
| --- | --- |
| The reply was for the user | Nothing; the reply is the answer. |
| The reply addressed an agent that was not invited | Who was addressed, and how to include it next time, or that it is unavailable. |
| The turn limit was reached | The limit, who the last reply was addressed to, and that it was not delivered. |
| A turn failed | The failure, as today. Nobody else is tried. |
| The user cancelled | That the exchange was cancelled, and during or before whose turn. This is the reason whenever the user cancelled, including between two turns and at a pause. |
| The user stopped it at a pause | That the handover was not delivered. |
| The reply addressed its own author, or handed over with no text | That there was nothing to deliver. |

After a stop the room is as ADR 0001 leaves it. Sending `@codex continue` starts a new exchange
with a full count, and Codex is shown whatever it missed.

### Stopping it yourself

- Ctrl+C cancels the **exchange**, not only the turn in flight. One cancellation covers the whole
  chain, so there is no gap between turns in which Ctrl+C would exit Relay instead.
- Cancelling kills the running agent's process tree, as now, and no further turn starts.
- Closing Relay (terminal closed, SIGTERM, SIGHUP) cancels the exchange the same way.
- The user cannot type while a turn runs; the chat loop does not read input then. Between turns
  they can, in pause mode.

### Pause mode

`/pause on` and `/pause off` set pause mode for the session; `/pause` alone says which it is. It
is not a toggle, so typing it twice cannot quietly turn a safety setting off, and anything else
after `/pause` changes nothing and says so. It is off when Relay starts. While it is on, a handover that would be delivered is shown and then held, and Relay asks what to
do with it:

| The user types | What happens |
| --- | --- |
| `/go` | The handover is delivered as written. |
| `/stop` | The exchange ends. Nothing is delivered. |
| Anything else | It is sent to the **author** of the handover as the user's message, in the same exchange. The author replies again, and if that reply hands over, Relay pauses again. |

- **The user never edits an agent's message.** Text labelled as Claude's is always exactly what
  Claude wrote. To change a handover the user tells its author what to change, and the author
  writes a new one.
- A handover the user sent back is not withdrawn. It stays in the room, addressed to the agent it
  was meant for, followed by the user's request and the author's new reply. The recipient is shown
  all three in order when a handover is finally delivered, under ADR 0001: nothing in a room is
  private.
- The user's request is a user message like any other. The author receives it after the
  transcript, where the user's words always go.
- The turn limit is unchanged. Each reply the user asks for uses a turn, and a pause only happens
  when there is a turn left to deliver into.
- Delivery takes a typed `/go` rather than an empty line, because the console keeps keys pressed
  during a turn: an Enter typed while waiting would otherwise approve a message nobody had read.
  An empty line asks again.
- Only `/go` and `/stop`, typed on their own, are commands at a pause. Any other line that starts
  with `/` (`/go ahead`, `/stop now`) is refused and asked again, never sent to an agent: a
  near-miss must not cost a turn. So is a line that begins by naming an agent other than the
  author (`@codex go on`), which was probably meant to send the handover on.
- A pause waits as long as the user does, but it can be interrupted: Ctrl+C at the prompt cancels
  the exchange, and closing Relay does not wait for an agent, because none is running.
- The pause prompt is read like any other line, so it works with piped input and can be tested
  end to end. End of input counts as `/stop`.
- The setting can stay on between messages because it only restricts. A switch that granted
  something would not be allowed to (see `/collab on` below).

### What the user sees

- Each reply is shown when it arrives, not when the exchange ends, with who it was addressed to.
- Before each turn the waiting line names the agent and the turn (`turn 2 of 6`, with the session's
  own limit after "of").

## Alternatives considered

| Option | Why not |
| --- | --- |
| Route every reply that begins with a mention, always | One injected reply could start another agent with no decision by the user. The issue asks for explicit authorisation. |
| Invite any agent mentioned anywhere in the user's message (`@claude ask @codex to ...`) | Reads naturally, but pasted text containing `@codex` would invite it, and "what did `@codex` get wrong?" would too. |
| A room-wide switch (`/collab on`) | Stays on after the user has stopped thinking about it. Per message, the authorisation is in front of them each time. |
| Always ask the user to approve each handover | Safest, and available as pause mode. As the only behaviour it is close to what they do today by typing `@codex` themselves. |
| Let the user edit a handover at a pause | The recipient would be shown the user's words under an agent's name, and a terminal line is a poor editor for a long message. |
| Deliver on an empty line at a pause | A key pressed during the previous turn would approve it. |
| Give the user a fresh turn count for each message typed at a pause | Simpler to state one rule: an exchange is at most the session's limit of turns, however it is steered. |
| Put the handed-over message after the transcript, where the user's message goes | An agent's words would sit where the recipient is told the user speaks. |
| Let an agent address several agents at once | Turns a chain into a tree, needs concurrent turns in one working folder, and multiplies cost. |
| Detect loops by repetition or lack of progress | Guesswork. The count is the bound and is simple to explain. |
| Limit by elapsed time or by cost | Time per turn is #29. Cost is not reported the same way by both CLIs. |
| Count turns per agent rather than per exchange | The bound would then grow with the number of agents. |
| Let the last agent's undelivered handover run when the limit is hit | Then the limit is not the limit. |
| Have Relay pick who speaks next (round robin) | That is a collaboration strategy, which belongs with roles in #9. |

## Consequences

- **"Agents never trigger each other" stops being true.** It becomes: an agent can start another
  agent's turn only inside an exchange the user opened by naming both, and at most one time fewer
  than the session's turn limit per message. `docs/threat-model.md` and `AGENTS.md` change in the
  same pull request as the code.
- **The user no longer reads every reply before the next agent acts on it.** Inside an exchange a
  reply steered by something the agent read reaches the next agent unreviewed. Attribution still
  cannot be forged and the reply never passes for the user's, but as ADR 0001 says, that stops
  forgery, not persuasion. What bounds the damage is the invitation, the count, and Ctrl+C.
  Pause mode puts the review back: with it on, no agent acts on another's reply until the user
  has sent it on.
- **Pausing is a decision the router asks for, not console code.** `Relay.Core` takes a callback
  that answers deliver, stop, or send this back to the author; the chat supplies the prompt.
- **One message can cost up to 6 agent turns** instead of one, or up to the limit the user chose
  at start-up, at most 20.
- **Naming two agents is not a broadcast.** Someone who types `@claude @codex thoughts?` and
  expects two answers gets Claude's, and Codex's only if Claude hands over.
- **A handover cut off by the limit is not lost.** It is in the room and reaches its addressee the
  next time the user addresses that agent.
- **The router returns a sequence of messages and a stop reason**, not one reply, and reports each
  message as it is recorded so the chat can show it.
- **Routing is not merged without the limit.** The two issues may be separate pull requests, but
  the count lands with or before the first routed reply.
- Roles and speaking order (#9), streaming (#10) and approvals (#11) are untouched.

## Amendment: the user chooses the limit (#62, 2026-10-18)

The limit above stays 6 by default. `relay start --max-turns <N>` sets it for the session instead,
where N is a whole number from 1 to **20**. Everything else in this record holds unchanged.

- **20 is the bound** because every turn is another agent process that runs with the user's
  permissions, and with pause mode off nobody need be watching any of them. Ten rounds between two
  agents covers implement, review, fix and re-review with room to spare; past that the user is
  better served by reading the room and sending another message. The bound is enforced in
  `ExchangeLimits`, so it holds for any caller, not only the command line.
- **Only the user sets it, and only at start-up.** The value is checked before the room opens and
  the router is built once with it. There is no command, setting file, environment variable or
  workspace file that changes it, and nothing an agent writes can raise it. To run longer, the
  user restarts Relay. This keeps the decision in front of the person who pays for the turns, as
  the rest of this record requires.
- A limit of 1 is allowed and means a handover is never delivered: the first agent answers and the
  exchange stops with the usual notice. The waiting line still shows `turn 1 of 1`, so the user can
  see why.
- What the user is told: `/help` states the session's limit, the header states it when it was
  chosen, and the notice at the limit says how to allow more, except at 20 where it says that is
  the most Relay allows.
