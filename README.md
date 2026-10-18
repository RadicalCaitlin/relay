# Relay

Your agents. One conversation.

Relay is a local, cross-platform terminal chatroom for AI coding agents. It discovers the agent
CLIs you already have installed (Claude Code and OpenAI Codex), puts them in a shared room with
you, and routes your messages to the agent you address with an `@mention`.

Relay drives each agent through its own official CLI. It needs no API keys of its own, runs no
backend server, and never changes an agent's permission or sandbox settings.

```
╭──────────────────────────────────────╮
│                RELAY                 │
│    Your agents. One conversation.    │
╰──────────────────────────────────────╯

● Claude       connected
● Codex        connected

# general  /help for commands

You: @claude Say hello to Relay.
Claude: Hello, Relay!

You: @codex What is 41 + 1? Reply with only the number.
Codex: 42
```

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows, macOS, or Linux
- At least one agent CLI, installed on your `PATH` and already signed in:
  - Claude Code (`claude`)
  - OpenAI Codex (`codex`)

Relay still runs without them; missing agents are shown as unavailable.

## Build and test

```
dotnet build
dotnet test
```

## Run

```
dotnet run --project src/Relay.Cli -- <command>
```

| Command           | Description                                              |
| ----------------- | -------------------------------------------------------- |
| `relay`           | Home screen: banner, agent status, and available commands |
| `relay agents`    | List agent providers, install status, version, and path  |
| `relay start`     | Start an interactive chatroom                            |
| `relay start --trust-workspace` | Start a chatroom, trusting the current folder for that session only |
| `relay start --workspace <path>` | Start a chatroom whose agents work in that folder instead of the current one |
| `relay start --agents claude,codex` | Start a chatroom with only the agents named; each must be installed |
| `relay start --turn-timeout 30m` | Start a chatroom in which an agent whose turn runs longer than 30 minutes is stopped (`90s`, `30m`, `2h`; at most `24h`) |
| `relay trust`     | Trust the current folder and remember it (`--remove` to undo, `--list` to show all) |
| `relay --version` | Print the Relay version                                  |
| `relay --help`    | Show help                                                |

Inside the chatroom:

| Input               | Effect                                              |
| ------------------- | --------------------------------------------------- |
| `@claude <message>` | Send the message to Claude and show its reply       |
| `@codex <message>`  | Send the message to Codex and show its reply        |
| `@claude @codex <message>` | Send the message to Claude, and let the agents named pass messages to each other |
| any other text      | Record the message in the room; agents see it the next time you address them. A line whose first word is `/word` is always treated as a command, never recorded |
| a paste of several lines | One message, with its line breaks (see below)   |
| `/agents`           | Show the agents in this room                        |
| `/history`          | Replay the room's message history                   |
| `/clear`            | Clear the display (history is kept)                 |
| `/restart`          | Start a new conversation with every agent, showing each the room again |
| `/restart <agent>`  | The same for one agent; use it when an agent's conversation is broken |
| `/reset`            | Start a new conversation with every agent from a clean slate |
| `/reset <agent>`    | The same for one agent: it is shown nothing said before the reset |
| `/pause on`, `/pause off` | Pause mode: ask before one agent's message is passed to another. `/pause` alone shows which it is |
| `/approvals on`, `/approvals off` | Be asked before a step an agent's own settings do not allow. `/approvals` alone shows which it is |
| `/auto on`, `/auto off` | Run Claude in its own auto mode from the next turn, so it asks you far less (see below). `/auto` alone shows which it is |
| `/role`             | Show each agent's role and the roles available     |
| `/role <agent> <role>` | Give an agent a role: `implementer`, `reviewer`, `architect` or `coordinator` |
| `/role <agent> <name>: <description>` | Give an agent a role of your own       |
| `/role <agent> none` | Remove an agent's role                            |
| `/help`             | Show the command list                               |
| `/exit`             | Leave the chatroom                                  |
| `Ctrl+C`            | Cancel the request or exchange in flight; exit when idle |

### Messages of several lines

Lines that arrive together are one message. Pasting several lines sends them as a single message
with its line breaks, and Relay says how many lines it is sending before it sends them. Typing a
message over several lines is not possible yet: Enter always sends (#67).

- Only the **first** line is read as a `/command` or for the `@agents` the message is addressed to.
  A `/command` pasted on a later line is text, and a mention there addresses nobody.
- If the first line of a paste *is* a command, it runs with only that line, and Relay says how many
  lines it did not use.
- The answer to an approval question, and the choice at a pause, are never taken from input that
  arrived as several lines. The whole block is dropped and the question asked again.
- With input from a pipe or a file, every line is its own message, as before.

Relay tells a paste from typing by how fast the lines arrive, because a terminal cannot tell
`Console.ReadLine` that a paste is happening. A paste that arrives slowly — over a slow connection,
or on a loaded machine — can still split, and the lines after the split are read as if you had
typed them, so a `/command` among them runs. The line count Relay prints before it sends is how you
see that this has happened.

### One shared conversation

Everything said in a room, by you or by an agent, is shown to each agent the next time you address
it. If you tell Codex something and then ask Claude about it, Claude has seen both your message and
Codex's reply, each labelled with who said it.

- Nothing in a room is private from its agents, including what you said to a different agent.
- An agent only speaks when you address it, or when another agent addresses it in an exchange you
  opened to both (see below).
- Relay replays at most the 20 most recent messages an agent has missed, and tells it how many
  older ones were left out.
- `/restart <agent>` starts a new conversation with that agent and shows it the room again.
  `/reset <agent>` also makes it forget everything said before the reset.

See [docs/adr/0001-shared-room-context.md](docs/adr/0001-shared-room-context.md) for the design.

### Letting agents talk to each other

Name more than one agent at the start of a message to open an **exchange**:

```
You: @claude @codex agree on a queue design
Claude to Codex: @codex
I propose a channel. Objections?
Codex: None. A channel it is.
```

- The first agent named answers. The others speak only if an agent hands over to them, by putting
  their `@name` alone on the first line of its reply. Naming two agents does not ask both for an
  answer.
- Any other reply comes back to you and ends the exchange, including one that merely begins by
  mentioning an agent (`@claude and I agree...`).
- An exchange is at most 6 agent turns. Relay says why it stopped whenever the reason is anything
  other than a reply to you.
- Only the mentions your message begins with count, and only while they name agents in the room.
  `@claude ask @codex to review` and `@claude @Override is missing` both go to Claude alone, as
  typed. Outside an exchange nothing an agent writes is delivered to another agent.
- Agents cannot invite other agents, raise the limit, or start an exchange of their own.
- `Ctrl+C` stops the whole exchange.
- After `/pause on`, Relay shows each message one agent addresses to another and waits: `/go`
  passes it on, `/stop` ends the exchange, and anything else you type goes back to its author as
  your message, so it can write a better one. You never edit an agent's message yourself. A line
  that starts with `/` or with another agent's name is never sent; Relay asks again.

Inside an exchange with pause mode off, you do not read a reply before the next agent acts on it.
See [docs/adr/0002-agent-to-agent-exchanges.md](docs/adr/0002-agent-to-agent-exchanges.md) for the
design and [docs/threat-model.md](docs/threat-model.md) for what that means.

### Roles

You can tell an agent what to be in the room, so you do not have to repeat it in every message:

```
You: /role claude implementer
You: /role codex reviewer
You: @claude @codex add retry to the uploader
```

- The built-in roles are `implementer`, `reviewer`, `architect` and `coordinator`. Any agent can
  hold any of them; none is tied to Claude or Codex.
- `/role codex tester: you write a failing test before any fix` gives a role of your own, with a
  description of up to 500 characters.
- Relay tells the agent its role on every turn, and in an exchange tells each agent the others'
  roles. An agent with no role is sent your message exactly as before.
- **A role is an instruction, not a restriction.** A reviewer can still edit files if its own
  configuration lets it. Relay never changes what an agent is allowed to do.
- Relay does not decide who speaks next. Roles inform the agents; the one speaking still chooses
  whether to hand over.
- Roles last for the session, including across `/restart` and `/reset`, and are gone when Relay exits.

See [docs/adr/0003-agent-roles.md](docs/adr/0003-agent-roles.md) for the design.

### Watching an agent work

A turn can take minutes. While Relay waits it shows what the agent is doing, as it happens:

```
You: @claude add retry to the uploader
Waiting for Claude... (Ctrl+C to cancel)
  - Looking at how uploads are sent today.
  - Read: src/Uploader.cs
  - Write: src/Retry.cs
  ! Write was refused without asking you: approvals are off. /approvals on turns them on.
  - PowerShell: dotnet test
Claude: I could not add src/Retry.cs: writing files needs your approval.
```

- A line starting with `-` is something the agent said along the way, or a step it took: a file it
  read or changed, a command it ran.
- A line starting with `!` is a step that was refused or that failed. With approvals on, in a
  terminal, Claude asks you first instead (see "Approving a step").
- These lines are only for you. They are not part of the room: `/history` does not show them and
  no other agent sees them.
- Claude's own reasoning is not shown, because Claude Code does not make it available. Codex
  reports what it says and the commands it runs.
- The tool a step used is in bold, and a skill is shown with the skill it ran, as in
  `Skill: code-review`.

See [docs/adr/0004-agent-activity.md](docs/adr/0004-agent-activity.md) for the design.

### How replies are shown

Agents write Markdown, and Relay shows the common parts of it: headings in bold, `•` for bullet
points, numbered lists, **bold**, *italic*, `code` in its own colour, code blocks behind a `│`
with every line exactly as written, and quotes dimmed. Tables, links and anything else are shown
as written. An `@mention` is highlighted only where Relay acted on it: the agents your message
was sent to, and an agent's handover to another inside an exchange. `/history` shows
messages the same way.

The styles are Relay's own. Whatever a reply contains, it cannot choose colours, make links, or
send anything to your terminal but text; see [docs/threat-model.md](docs/threat-model.md).

### Approving a step

Each agent is limited by its own settings. When Claude wants to take a step those settings do not
allow, Relay asks you, and the step happens only if you type `yes`:

```
You: @claude save the date to today.txt
Waiting for Claude... (Ctrl+C to cancel)
  - PowerShell: Get-Date | Out-File today.txt
  ? Claude wants to use PowerShell:
      command: Get-Date | Out-File today.txt
      description: Save the date to today.txt
    Allow it this once? Type yes or no. Ctrl+C cancels the turn.
You: yes
Claude: Saved the date to today.txt.
```

- **A yes is for that one step.** Relay does not remember it and never changes an agent's
  settings. To stop being asked about something, allow it in the agent's own settings, or switch
  Claude into its auto mode (below).
- **Only `yes` or `y` allows.** `no` refuses, and Claude is told. An empty line, or anything else,
  is asked again and is not sent to anyone.
- **Read the command, not the description.** The description is the agent's own. A command is
  always shown in full; a longer value, such as a file's content, is cut short and says so.
- **Claude can also ask you a question**, with options to pick by number or an answer of your own.
  `/skip` leaves it unanswered.
- **Your answers are for that agent only.** They are not part of the room.
- **`/approvals off`** refuses every such step without asking, for when you are not watching.
  A turn otherwise waits for your answer for as long as you take.
- **Only in a terminal, and only Claude.** With input or output redirected nobody is asked and
  the step is refused, as before. Codex cannot ask: a step its own settings do not allow fails.

See [docs/adr/0005-approvals.md](docs/adr/0005-approvals.md) for the design.

### Auto mode

Being asked about every step soon gets in the way. `/auto on` runs Claude in Claude Code's own
auto mode from its next turn: a classifier model decides the steps Claude's settings leave open,
and only what it will not decide is put to you. `/auto off` goes back, for example to approve one
thing yourself; you can switch as often as you like, between turns.

- **Your rules still hold.** A deny rule still denies, and an ask rule, such as one for
  `gh pr merge`, still asks you.
- **It is a trade.** The classifier is meant to block steps that go beyond your request or that
  something hostile put Claude up to, but it can be wrong. With agents passing work to each other,
  `/pause on` lets you read each handover first. See [docs/threat-model.md](docs/threat-model.md).
- **Claude only.** Codex has no auto mode Relay can switch on.
- **Not always available.** Auto mode depends on your plan, model and settings, and on a Claude
  Code version that has it. When Claude cannot use it, or does not say which mode it ran in, Relay
  says so once for that message, and steps may be put to you as before. A Claude Code from before
  auto mode fails every turn instead; `/auto off` puts it back.
- Each step the classifier reviews is a model call on your account.

See [docs/adr/0006-claude-auto-mode.md](docs/adr/0006-claude-auto-mode.md) for the design.

### Folder trust

Agents run in the folder you start Relay from, with whatever access your own agent configuration
grants them there.

By default Relay treats that folder as **untrusted**: Claude loads only your user-level settings
and ignores the folder's own hooks, MCP servers, settings and `CLAUDE.md`. This matters because a
folder's hooks and MCP servers run commands on your machine, and Claude Code does not show its
usual trust prompt when Relay starts it.

The first time you run `relay start` in a folder, or name it with `--workspace`, it asks whether
you trust the files there. A yes is remembered, so you are asked once per folder; `relay trust
--remove` undoes it. Trust only folders you would run code from. The chatroom header shows which
mode is in effect.

This setting governs Claude only. Codex applies its own trust checks: in a folder you have never
trusted in Codex it runs none of the folder's hooks or MCP servers, but it does read the folder's
`AGENTS.md` and skills. A folder you have trusted in Codex itself stays trusted there, whatever
you told Relay.

See [docs/threat-model.md](docs/threat-model.md) for the full security model and remaining risks.

## Agent support

| Agent        | Discovery | Chat | How Relay talks to it                                                  |
| ------------ | --------- | ---- | ---------------------------------------------------------------------- |
| Claude Code  | yes       | yes  | `claude -p --output-format stream-json --verbose`, continued with `--resume <id>`. In a terminal, also `--mcp-config <file> --permission-prompt-tool <tool>`, so it can ask you. With `/auto on`, also `--permission-mode auto` |
| OpenAI Codex | yes       | yes  | `codex exec --json -`, continued with `codex exec resume <id> --json -` |

Both integrations were verified against Claude Code 2.1.273 and codex-cli 0.162.0 on Windows.

## MVP limitations

- One room (`general`), held in memory. Nothing is persisted between runs.
- There are no private messages: every agent in a room sees everything said in it.
- Because agents now read each other's replies, one agent can influence another through the room.
  See the threat model.
- A reply arrives whole when the agent finishes. While you wait, Relay shows what the agent is
  doing (see "Watching an agent work"), but not the reply as it is written.
- Only Claude can ask you before a step its own settings do not allow, and only when Relay runs
  in a terminal (see "Approving a step"). Codex cannot: such a step fails, and Codex does not
  report it as a refusal. Codex also refuses to run outside a Git repository.
- An agent's turn has no time limit unless you start Relay with `--turn-timeout`. Without one, press
  `Ctrl+C` to cancel a request that hangs. With one, an agent that runs past it is stopped, Relay
  says so, and the agent keeps its conversation. Time spent waiting for your answer to an approval
  question counts towards the limit.
- You cannot type while an agent is working, so an exchange can only be interrupted with `Ctrl+C`
  or, in pause mode, between turns.
- One agent speaks at a time, and an exchange between agents is limited to 6 turns.
- Not yet packaged; run it with `dotnet run`.

## Near-term roadmap

- Streaming the reply itself as it is written
- Multiple rooms and persisted history
- Packaging as a .NET global tool and native executables

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to report a bug or propose a change, and
[AGENTS.md](AGENTS.md) for architecture, conventions, and the rules for process execution.
Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

To have Git refuse a commit that contains a secret, install
[gitleaks](https://github.com/gitleaks/gitleaks#installing) and enable the hook once per clone:

```
git config core.hooksPath .githooks
```

CI runs the same scan on the commits of every pull request.

## Security

To report a vulnerability, follow [SECURITY.md](SECURITY.md); please do not open a public issue.
[docs/threat-model.md](docs/threat-model.md) describes what Relay protects against and what it does not.

## Licence

Relay is released under the [MIT License](LICENSE).
