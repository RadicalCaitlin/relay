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
| any other text      | Record the message in the room; agents see it the next time you address them |
| `/agents`           | Show discovered agents                              |
| `/history`          | Replay the room's message history                   |
| `/clear`            | Clear the display (history is kept)                 |
| `/restart`          | Start a new conversation with every agent, showing each the room again |
| `/restart <agent>`  | The same for one agent; use it when an agent's conversation is broken |
| `/reset`            | Start a new conversation with every agent from a clean slate |
| `/reset <agent>`    | The same for one agent: it is shown nothing said before the reset |
| `/help`             | Show the command list                               |
| `/exit`             | Leave the chatroom                                  |
| `Ctrl+C`            | Cancel the request in flight; exit when idle        |

### One shared conversation

Everything said in a room, by you or by an agent, is shown to each agent the next time you address
it. If you tell Codex something and then ask Claude about it, Claude has seen both your message and
Codex's reply, each labelled with who said it.

- Nothing in a room is private from its agents, including what you said to a different agent.
- An agent only speaks when you address it. Agents never trigger each other.
- Relay replays at most the 20 most recent messages an agent has missed, and tells it how many
  older ones were left out.
- `/restart <agent>` starts a new conversation with that agent and shows it the room again.
  `/reset <agent>` also makes it forget everything said before the reset.

See [docs/adr/0001-shared-room-context.md](docs/adr/0001-shared-room-context.md) for the design.

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
- One agent per message, and agents never trigger each other.
- Not yet packaged; run it with `dotnet run`.

## Near-term roadmap

- Controlled agent-to-agent collaboration with turn limits and loop prevention
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
