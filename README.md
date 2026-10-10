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
| any other text      | Record the message in the room without sending it   |
| `/agents`           | Show discovered agents                              |
| `/history`          | Replay the room's message history                   |
| `/clear`            | Clear the display (history is kept)                 |
| `/reset`            | Start a new conversation with every agent           |
| `/reset <agent>`    | Start a new conversation with one agent             |
| `/help`             | Show the command list                               |
| `/exit`             | Leave the chatroom                                  |
| `Ctrl+C`            | Cancel the request in flight; exit when idle        |

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
- An agent sees only the messages addressed to it, not the rest of the room. Each agent keeps
  its own conversation for the lifetime of the room, or until you `/reset` it.
- Replies arrive whole when the agent finishes; there is no streaming.

- Agents cannot answer permission prompts through Relay. Tool use that would need approval is
  declined by the agent CLI, so agents are effectively limited to what their own configuration
  already allows. Codex also refuses to run outside a Git repository.
- An agent's turn has no time limit. If an agent hangs, press `Ctrl+C` to cancel the request.
- One agent per message, and agents never trigger each other.
- Not yet packaged; run it with `dotnet run`.

## Near-term roadmap

- Shared room context, so agents can see what others said
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
