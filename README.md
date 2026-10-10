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
| `/agents`           | Show discovered agents                              |
| `/history`          | Replay the room's message history                   |
| `/clear`            | Clear the display (history is kept)                 |
| `/restart`          | Start a new conversation with every agent, showing each the room again |
| `/restart <agent>`  | The same for one agent; use it when an agent's conversation is broken |
| `/reset`            | Start a new conversation with every agent from a clean slate |
| `/reset <agent>`    | The same for one agent: it is shown nothing said before the reset |
| `/pause on`, `/pause off` | Pause mode: ask before one agent's message is passed to another. `/pause` alone shows which it is |
| `/help`             | Show the command list                               |
| `/exit`             | Leave the chatroom                                  |
| `Ctrl+C`            | Cancel the request or exchange in flight; exit when idle |

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

### Folder trust

Agents run in the folder you start Relay from, with whatever access your own agent configuration
grants them there.

By default Relay treats that folder as **untrusted**: Claude loads only your user-level settings
and ignores the folder's own hooks, MCP servers, settings and `CLAUDE.md`. This matters because a
folder's hooks and MCP servers run commands on your machine, and Claude Code does not show its
usual trust prompt when Relay starts it.

The first time you run `relay start` in a folder it asks whether you trust the files there. A yes
is remembered, so you are asked once per folder; `relay trust --remove` undoes it. Trust only
folders you would run code from. The chatroom header shows which mode is in effect.

See [docs/threat-model.md](docs/threat-model.md) for the full security model and remaining risks.

## Agent support

| Agent        | Discovery | Chat | How Relay talks to it                                                  |
| ------------ | --------- | ---- | ---------------------------------------------------------------------- |
| Claude Code  | yes       | yes  | `claude -p --output-format json`, continued with `--resume <id>`       |
| OpenAI Codex | yes       | yes  | `codex exec --json -`, continued with `codex exec resume <id> --json -` |

Both integrations were verified against Claude Code 2.1.273 and codex-cli 0.162.0 on Windows.

## MVP limitations

- One room (`general`), held in memory. Nothing is persisted between runs.
- There are no private messages: every agent in a room sees everything said in it.
- Because agents now read each other's replies, one agent can influence another through the room.
  See the threat model.
- Replies arrive whole when the agent finishes; there is no streaming.

- Agents cannot answer permission prompts through Relay. Tool use that would need approval is
  declined by the agent CLI, so agents are effectively limited to what their own configuration
  already allows. Codex also refuses to run outside a Git repository.
- An agent's turn has no time limit. If an agent hangs, press `Ctrl+C` to cancel the request.
- You cannot type while an agent is working, so an exchange can only be interrupted with `Ctrl+C`
  or, in pause mode, between turns.
- One agent speaks at a time, and an exchange between agents is limited to 6 turns.
- Not yet packaged; run it with `dotnet run`.

## Near-term roadmap

- Agent roles and collaboration strategies
- Streaming replies and tool-activity display
- Surfacing agent permission prompts to the human
- Multiple rooms and persisted history
- Packaging as a .NET global tool and native executables

## Contributing

See [AGENTS.md](AGENTS.md) for architecture, conventions, and the rules for process execution.

To have Git refuse a commit that contains a secret, install
[gitleaks](https://github.com/gitleaks/gitleaks#installing) and enable the hook once per clone:

```
git config core.hooksPath .githooks
```

CI runs the same scan on the commits of every pull request.

## Licence

Relay is released under the [MIT License](LICENSE).
