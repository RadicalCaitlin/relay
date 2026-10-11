# ADR 0003: Agent roles

- **Status:** Accepted
- **Date:** 2026-10-10
- **Issue:** #9

## Context

Since ADR 0002 the user can put two agents in an exchange, but has to say in every message who
should do what: "Claude: write it. Codex: review it." Nothing about that is tied to a message. It
is how the user wants the room to work.

Issue #9 asks for optional roles such as implementer, reviewer, architect and coordinator, that
the user can select and configure, and that are not hardwired to Claude or Codex. ADR 0002 also
left a question for this issue: whether Relay itself should decide who speaks next.

Three things constrain the answer:

- Relay cannot limit what an agent does. An agent acts with whatever its own configuration allows.
  A role can only ever be something the agent is told.
- Whatever an agent is told about the room has to come from Relay, in the introduction that
  `SharedContext` writes, not from another agent.
- Relay keeps nothing between runs. Stored configuration is #13.

## Decision

**A role is a name and a description that the user gives to an agent for the session. Relay tells
the agent its role, in Relay's own words, on every turn. A role changes what the agent is asked to
be; it changes nothing about routing, turn limits or permissions.**

### What a role is

- A role has a short name (`reviewer`) and a description of a sentence or two.
- It belongs to an agent's place in the room, not to a provider. Any agent can hold any role, and
  no role text mentions Claude or Codex.
- An agent has one role or none. Two agents may hold the same role.
- Roles are optional. An agent with no role is sent exactly what it is sent today.

### Built-in roles

| Name | What the agent is told |
| --- | --- |
| `implementer` | You write and change the code. Make the smallest change that does what was asked, run the build and tests, and say what you did and what you could not verify. |
| `reviewer` | You review what others propose or change. Judge the change itself before you read its author's account of it. Look first for defects: what the old code guaranteed that the new code does not, what is left behind when a step fails, and what differs between operating systems. Then for missing tests. For each finding give the place, a concrete scenario, and whether you verified it. Say what must change and what is optional. Do not make the changes yourself unless you are asked to. |
| `architect` | You decide structure before code is written: components, boundaries and trade-offs. Give a recommendation with your reasons, and leave the implementation to others unless you are asked to do it. |
| `coordinator` | You break the task into parts, hand each part to the agent best placed to do it, and report the combined result to the user. Do little of the work yourself. |

### Selecting and configuring

| The user types | What happens |
| --- | --- |
| `/role` | Shows each agent's role and lists the roles available. |
| `/role claude reviewer` | Gives Claude a built-in role. |
| `/role claude tester: you write a failing test before any fix` | Gives Claude a role of the user's own: the name before the colon, the description after. |
| `/role claude none` | Removes Claude's role. |

- A role lasts for the session. It survives `/restart` and `/reset`, which are about the agent's
  conversation, not about the room.
- Saving roles between runs, and starting Relay with roles already set, belongs with #13.
- An agent that is unavailable can be given a role; it simply never takes a turn.

### What the agent is sent

- While an agent has a role, Relay's introduction states it on every turn, before anything from
  the room: the name, the description, and that the role comes from the user.
- A turn for an agent with a role therefore always has an introduction, even when there is
  nothing to catch up on. The user's message still comes last, after it.
- When a role is removed, the agent's next turn says once that it no longer has one. Its own
  conversation still holds the old instruction, so silence would leave it in force. An agent that
  was never sent the role is told nothing.
- In an exchange, the list of agents names each one's role (`Claude (@claude, implementer)`), so
  an agent knows who it is handing over to.
- The role is stated every turn rather than once, so it does not depend on what the agent
  remembers: a new conversation, a failed turn or a provider that forgets all get it again.

### When it cannot be done

| Case | What happens |
| --- | --- |
| The agent named is not in the room | A notice listing the agents. Nothing changes. |
| The role named is not built in and no description was given | A notice listing the built-in roles and showing the custom form. Nothing changes. |
| A custom name is not letters, digits and hyphens, or the description is empty or too long | A notice saying which. Nothing changes. |
| A custom name is the name of an agent in the room | A notice saying so. Nothing changes. In an exchange the role is listed beside the agent's `@id`, where it would read as an address. |
| A custom role uses a built-in name | It replaces that role's description for this agent only; the notice says so. |

A custom description is at most 500 characters, so a role cannot turn into a second prompt that is
sent on every turn.

### Who speaks next

**Relay still does not choose.** The agent that is speaking decides whether to hand over and to
whom, as in ADR 0002, and roles only inform that choice. There is no round robin, no automatic
"implement, then review" sequence, and no addressing by role (`@reviewer`).

A sequence that Relay drove would start turns the user did not ask for one by one, which is the
thing ADR 0002 bounded. With roles set, `@claude @codex add retry to the uploader` already
produces implement-then-review when the roles say so, inside the same six turns.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Tie default roles to providers (Claude implements, Codex reviews) | The issue rules it out, and which agent is better at what changes with every release. |
| State the role once and rely on the agent's memory | Lost on a new conversation, and never held by a provider that does not remember. Tracking who was told what is more machinery than a sentence per turn. |
| Put the role in a system prompt flag | Claude has one, Codex does not, and a resumed conversation keeps the system prompt it started with. Same reasons as in ADR 0001. |
| Enforce roles (a reviewer cannot edit files) | Relay never touches an agent's permissions or sandbox, and could not make it true for both CLIs. Saying it would be a promise Relay cannot keep. |
| Address by role (`@reviewer look at this`) | With two agents it saves nothing over `@codex`, and it adds rules: nobody holds the role, two agents hold it, a role named like an agent. Worth revisiting with more agents. |
| Relay-driven strategies (round robin, pipelines) | See "Who speaks next". |
| Read roles from a file in the working folder | The folder is untrusted by default. Its text would be sent to every agent on every turn as if the user had written it. |
| Let an agent propose or change roles | `/commands` are only read from the user. An agent that could rewrite another's role could steer it. |
| Roles per message (`@claude:reviewer ...`) | More to type each time, which is the problem roles are meant to remove. |

## Consequences

- **A role is an instruction, not a restriction.** A reviewer can still edit files if its own
  configuration lets it. The README and `/role` say so.
- **An agent with a role is no longer sent the user's message exactly as typed.** It gets Relay's
  introduction first, on every turn, at a cost of a few dozen tokens.
- **Custom role text is the user's, sent in Relay's voice.** It is typed by the user and never
  comes from an agent or from the folder, so it adds no new untrusted input. It is escaped like
  any other text in the introduction.
- **Roles are lost when Relay exits**, like everything else in a room, until #13.
- **`coordinator` is only a description.** It works through ordinary handovers inside the turn
  limit. Nothing gives it more turns or more reach than any other agent.
- **Tests** cover assignment, replacement and removal, the role reaching the agent on every turn
  and after `/restart`, the exchange list, and each row of "When it cannot be done".
